using BroadcastRouter.Infrastructure;
using BroadcastRouter.Domain;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticCapacityRecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("before-commit", 102001)]
    [InlineData("after-commit", 101001)]
    public async Task CrashAtTrimBoundaryRetainsCommittedProgressAndResumes(string boundary, long expectedRows)
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings { ConfigurationRevision = 87 });
        await fixture.Scalar("""
            INSERT INTO sources VALUES('synthetic','{}','2025-01-01');
            INSERT INTO ports VALUES('synthetic','{}','synthetic','2025-01-01');
            INSERT INTO routes VALUES('synthetic','synthetic','WaitingForStream',1,'{}','2025-01-01');
            INSERT INTO route_history(source_id,previous_state,new_state,detail,timestamp_utc)
                VALUES('synthetic','Starting','WaitingForStream','essential canary','2025-01-01');
            INSERT INTO configuration_audit(timestamp_utc,event_type,entity_id,card_name,port_name,previous_state,new_state,actor,reason,backend_status,source_id)
                VALUES('2025-01-01','Save','synthetic','card','port','old','new','operator','essential canary','Running','synthetic');
            SELECT 1;
            """);
        await fixture.Seed(102001, "2024-11-01T00:00:00.0000000+00:00");
        await fixture.Store.StopDiagnosticsAsync();
        var before = await EssentialRows(fixture.Store.DatabasePath);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.OwnerHost",
            "bin", configuration, "net8.0", "BroadcastRouter.OwnerHost.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("trim-crash"); start.ArgumentList.Add(fixture.Store.DatabasePath); start.ArgumentList.Add(boundary);
        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("commit-boundary", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("", await errors);
            Assert.Equal(expectedRows, await fixture.Count("logs"));
            Assert.Equal(before, await EssentialRows(fixture.Store.DatabasePath));
            var resumed = new SqliteDataStore(fixture.Store.DatabasePath, readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
            try
            {
                await resumed.InitializeAsync();
                Assert.True(resumed.DiagnosticStatus.HistoricalIncomplete);
                Assert.True(resumed.EnqueueDiagnostic("Information", "Synthetic", "after-crash"));
                await resumed.FlushDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, resumed.DiagnosticStatus.Written);
                Assert.Equal(0, resumed.DiagnosticStatus.WriteFailures);
                Assert.True(await fixture.Count("logs") <= 100000);
                Assert.Equal(before, await EssentialRows(fixture.Store.DatabasePath));
            }
            finally { await resumed.StopDiagnosticsAsync(); }
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<string> EssentialRows(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        var rows = new List<string>();
        foreach (var table in new[] { "settings", "sources", "ports", "routes", "route_history", "configuration_audit" })
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(table + ":" + System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        }
        return string.Join("\n", rows);
    }

    [Theory]
    [InlineData(102001)]
    [InlineData(2000000)]
    public async Task RuntimeBacklogDoesNotRollBackPruningAndStarveNewDiagnostics(int rows)
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Seed(rows, "2024-11-01T00:00:00.0000000+00:00");
        Assert.True(fixture.Store.EnqueueDiagnostic("Information", "Synthetic", "after backlog"));
        using var catchupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        var catchupStart = Stopwatch.GetTimestamp();
        var flush = fixture.Store.FlushDiagnosticsAsync();
        var measurements = new List<double>();
        // Essential writes contend with the real catch-up worker, not a fake gate.
        do
        {
            var started = Stopwatch.GetTimestamp();
            await fixture.Store.SaveSettingsAsync(new OperatorSettings { ConfigurationRevision = measurements.Count + 1 }, catchupDeadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(2));
            measurements.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await Task.Yield();
        } while (!flush.IsCompleted);
        // The owner's catch-up ceiling is distinct from steady-state flush.
        await flush.WaitAsync(catchupDeadline.Token);
        output.WriteLine($"Rows={rows}; catchup_ms={Stopwatch.GetElapsedTime(catchupStart).TotalMilliseconds:F3}; essential_count={measurements.Count}; essential_max_ms={measurements.Max():F3}");
        Assert.All(measurements, ms => Assert.True(ms < 2000, $"Essential write {ms:F3} ms exceeded 2 s."));
        Assert.Equal(1, fixture.Store.DiagnosticStatus.Written);
        Assert.Equal(0, fixture.Store.DiagnosticStatus.WriteFailures);
        Assert.True(await fixture.Count("logs") <= 100000);
        Assert.Contains(await fixture.Store.ReadLogsAsync(), row => row.Message == "after backlog");
        Assert.True(fixture.Store.EnqueueDiagnostic("Information", "Synthetic", "after catchup"));
        var flushStart = Stopwatch.GetTimestamp();
        await fixture.Store.FlushDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"post_catchup_flush_ms={Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds:F3}");
        Assert.Equal(2, fixture.Store.DiagnosticStatus.Written);
    }

    [Fact]
    public async Task RuntimeCapacityProgressSurvivesRejectedInsert()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Seed(104001, "2024-11-01T00:00:00.0000000+00:00");
        await fixture.Scalar("CREATE TRIGGER reject_new_log BEFORE INSERT ON logs BEGIN SELECT RAISE(ABORT,'synthetic insert failure'); END; SELECT 1;");
        Assert.True(fixture.Store.EnqueueDiagnostic("Information", "Synthetic", "blocked insert"));
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync());
        Assert.Equal(100001, await fixture.Count("logs"));
        Assert.Equal(1, fixture.Store.DiagnosticStatus.WriteFailures);
        await fixture.Scalar("DROP TRIGGER reject_new_log; SELECT 1;");
        Assert.True(fixture.Store.EnqueueDiagnostic("Information", "Synthetic", "later success"));
        // Prior loss remains sticky, but later accepted records are persisted.
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync());
        Assert.Equal(1, fixture.Store.DiagnosticStatus.Written);
    }
}
