using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticRetentionTests
{
    [Fact]
    public async Task LegacyBacklogDoesNotScaleRetentionSelectionWorkWithEntireTable()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Seed(110000, "2024-11-01T00:00:00.0000000+00:00");
        var small = await fixture.MeasurePruneInstructions();
        await fixture.Seed(300000, "2024-11-01T00:00:00.0000000+00:00");
        var large = await fixture.MeasurePruneInstructions();
        Assert.True(large <= small + 200000,
            $"Retention selection scanned the legacy backlog: {small} versus {large} VM instructions.");
    }

    [Theory]
    [InlineData("message")]
    [InlineData("category")]
    [InlineData("source_id")]
    [InlineData("correlation_id")]
    [InlineData("level")]
    public async Task OversizedLegacyFieldNeverProducesHealthySuccessfulNoOp(string field)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Seed(2, "2024-11-01T00:00:00.0000000+00:00");
        await fixture.Scalar($"UPDATE logs SET {field}=zeroblob(62914560) WHERE id=1; SELECT 1;");
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.PruneDiagnosticLogsAsync());
        Assert.False(fixture.Store.DiagnosticStatus.IsHealthy);
        Assert.Equal(2, await fixture.Count("logs"));
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("write-failure")]
    [InlineData("unclean-drain")]
    [InlineData("abandoned-drain")]
    public async Task DiagnosticLossOrUncleanDrainRemainsVisibleAfterRestart(string failure)
    {
        await using var fixture = await Fixture.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (failure == "abandoned-drain")
        {
            var queue = new DiagnosticPersistenceQueue(async (_, _) => await release.Task);
            typeof(SqliteDataStore).GetField("_diagnostics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(fixture.Store, queue);
            Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "uncooperative storage"));
        }
        if (failure == "drop")
            Assert.False(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", new string('x', 8193)));
        if (failure == "write-failure")
        {
            fixture.Free = 0;
            Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "lost summary"));
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync());
        }
        if (failure != "unclean-drain") await fixture.Store.StopDiagnosticsAsync();
        var restarted = new SqliteDataStore(fixture.Store.DatabasePath, fixture.Clock,
            readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        await restarted.InitializeAsync();
        try { Assert.False(restarted.DiagnosticStatus.IsHealthy); }
        finally
        {
            release.TrySetResult();
            await fixture.Store.StopDiagnosticsAsync();
            await restarted.StopDiagnosticsAsync();
        }
    }

    [Fact]
    public async Task AdmissionRequiresDurableSessionInitialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-uninitialized-diagnostics-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"), readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        try { Assert.False(store.EnqueueDiagnostic("Warning", "Fixture", "no durable session yet")); }
        finally { await store.StopDiagnosticsAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MidTransactionHistoryFailureCannotPublishOrPersistRoute()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Scalar("CREATE TRIGGER reject_fixture_history BEFORE INSERT ON route_history BEGIN SELECT RAISE(ABORT,'fixture history write failed'); END; SELECT 1;");
        using var coordinator = new BroadcastRouter.Web.Services.RouterCoordinator(fixture.Store, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BroadcastRouter.Web.Services.RouterCoordinator>.Instance);
        var route = new RuntimeRoute("TEST/source", "Synthetic", "TEST-PORT", "Output", "1080i50", RouteState.Reserved,
            AssignmentMode.Manual, false, 0, 0, null, null, null, 0, 0, null, fixture.Clock.UtcNow, null, null);
        var replace = typeof(BroadcastRouter.Web.Services.RouterCoordinator).GetMethod("ReplaceRouteAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.ThrowsAsync<SqliteException>(async () => await (Task)replace.Invoke(coordinator, [route, null, CancellationToken.None, true])!);
        Assert.Empty(await fixture.Store.LoadRoutesAsync());
        Assert.Equal(0, await fixture.Count("route_history"));
        var routes = (System.Collections.IDictionary)typeof(BroadcastRouter.Web.Services.RouterCoordinator).GetField("_routes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Assert.Empty(routes);
    }

    [Fact]
    public async Task CleanDiagnosticDrainDoesNotReportHistoricalLossOnRestart()
    {
        await using var fixture = await Fixture.Create();
        Assert.True(fixture.Store.EnqueueDiagnostic("Information", "Fixture", "retained"));
        await fixture.Store.StopDiagnosticsAsync();
        var restarted = new SqliteDataStore(fixture.Store.DatabasePath, fixture.Clock,
            readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        await restarted.InitializeAsync();
        try { Assert.True(restarted.DiagnosticStatus.IsHealthy); }
        finally { await restarted.StopDiagnosticsAsync(); }
    }

    [Fact]
    public async Task HostedMaintenancePrunesExpiredDiagnosticsWithoutAnOperatorRequest()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Seed(20, "2024-11-01T00:00:00.0000000+00:00");
        var worker = new BroadcastRouter.Web.Services.DiagnosticPersistenceWorker(fixture.Store);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (await fixture.Count("logs") != 0 && deadline.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);
            Assert.Equal(0, await fixture.Count("logs"));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RetentionPrunesBoundedDiagnosticBatchesWithoutTouchingEssentialState()
    {
        await using var fixture = await Fixture.Create();
        var route = new RuntimeRoute("TEST/source", "Synthetic", "TEST-PORT", "Output", "1080i50", RouteState.Reserved,
            AssignmentMode.Manual, false, 0, 0, null, null, null, 0, 0, null, fixture.Clock.UtcNow, null, null);
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        await fixture.Store.SaveRouteAsync(route);
        await fixture.Store.WriteConfigurationAuditAsync(new(0, fixture.Clock.UtcNow, "Fixture", "TEST-PORT", "Card", "Output",
            "old", "new", "Synthetic", "Fixture", "Running"));
        await fixture.Seed(2000, "2024-11-01T00:00:00.0000000+00:00");
        await fixture.Seed(20, "2025-01-01T00:00:00.0000000+00:00");
        Assert.Equal(1000, await fixture.Store.PruneDiagnosticLogsAsync());
        Assert.Equal(1020, await fixture.Count("logs"));
        Assert.Equal(1000, await fixture.Store.PruneDiagnosticLogsAsync());
        Assert.Equal(20, await fixture.Count("logs"));
        Assert.Equal(0, await fixture.Store.PruneDiagnosticLogsAsync());
        Assert.Equal(route, Assert.Single(await fixture.Store.LoadRoutesAsync()));
        Assert.Single(await fixture.Store.ReadConfigurationAuditAsync());
        Assert.Equal(1, await fixture.Count("route_history"));
        Assert.Equal(1, await fixture.Count("settings"));
    }

    [Fact]
    public async Task RowCapRemovesOldestAndPersistsIncompleteSummaryAlarm()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Seed(100005, "2025-01-01T00:00:00.0000000+00:00");
        Assert.Equal(5, await fixture.Store.PruneDiagnosticLogsAsync());
        Assert.Equal(100000, await fixture.Count("logs"));
        Assert.Equal(6, await fixture.Scalar("SELECT MIN(id) FROM logs"));
        Assert.False(fixture.Store.DiagnosticStatus.IsHealthy);
        var restarted = new SqliteDataStore(fixture.Store.DatabasePath, fixture.Clock);
        await restarted.InitializeAsync();
        try { Assert.False(restarted.DiagnosticStatus.IsHealthy); }
        finally { await restarted.StopDiagnosticsAsync(); }
    }

    [Theory]
    [InlineData(100_000_000_000L, 19_999_999_999L)]
    [InlineData(500_000_000_000L, 99_999_999_999L)]
    [InlineData(10_000_000_000L, 9_000_000_000L)]
    public async Task LowDiskRefusesOptionalLogsButDoesNotAcknowledgeThem(long total, long free)
    {
        await using var fixture = await Fixture.Create();
        fixture.Total = total; fixture.Free = free;
        var bytes = fixture.Bytes;
        Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "must not allocate"));
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync());
        Assert.Equal(bytes, fixture.Bytes);
        Assert.Equal(0, await fixture.Count("logs"));
        Assert.False(fixture.Store.DiagnosticStatus.IsHealthy);
    }

    [Fact]
    public async Task LockedRealSqliteWriterDoesNotBlockAdmissionAndReportsFailedPersistence()
    {
        await using var fixture = await Fixture.Create();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Store.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO logs(timestamp_utc,level,category,message) VALUES('2025-01-01','Info','Fixture','uncommitted')";
        await command.ExecuteNonQueryAsync();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "locked DB fixture"));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, fixture.Store.DiagnosticStatus.WriteFailures);
        Assert.Equal(0, fixture.Store.DiagnosticStatus.Written);
        Assert.False(fixture.Store.DiagnosticStatus.IsHealthy);
        await transaction.RollbackAsync();
        Assert.Equal(0, await fixture.Count("logs"));
    }

    [Fact]
    public async Task NewBatchHonorsCeilingAndRetainsRecentMessageWithVisibleEarlyEviction()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Seed(100000, "2025-01-01T00:00:00.0000000+00:00");
        Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "new event"));
        await fixture.Store.FlushDiagnosticsAsync();
        Assert.Equal(100000, await fixture.Count("logs"));
        Assert.Equal("new event", (await fixture.Store.ReadLogsAsync()).First().Message);
        Assert.False(fixture.Store.DiagnosticStatus.IsHealthy);
    }

    [Fact]
    public async Task ImpossibleCapacityReadingRefusesOptionalAllocation()
    {
        await using var fixture = await Fixture.Create();
        fixture.Total = 0;
        Assert.True(fixture.Store.EnqueueDiagnostic("Warning", "Fixture", "unknown volume"));
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.FlushDiagnosticsAsync());
        Assert.Equal(0, await fixture.Count("logs"));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "router-retention-" + Guid.NewGuid().ToString("N"));
        public long Free = 80_000_000_000;
        public long Total = 100_000_000_000;
        public SupervisionClock Clock { get; } = new(new ManualTimeProvider());
        public SqliteDataStore Store { get; }
        public long Bytes => Directory.GetFiles(Root).Sum(path => new FileInfo(path).Length);
        private Fixture() => Store = new(Path.Combine(Root, "router.db"), Clock,
            readDiagnosticVolumeSpace: _ => new(Total, Free));
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture(); await fixture.Store.InitializeAsync(); return fixture;
        }
        private async Task<SqliteConnection> Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Store.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(); return connection;
        }
        public async Task Seed(int count, string timestamp)
        {
            await using var connection = await Open(); await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<$count)
                INSERT INTO logs(timestamp_utc,level,category,message)
                SELECT $time,'Information','Synthetic','fixture' FROM seq
                """;
            command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$time", timestamp);
            await command.ExecuteNonQueryAsync();
        }
        public Task<long> Count(string table) => Scalar("SELECT COUNT(*) FROM " + table);
        public async Task<long> MeasurePruneInstructions()
        {
            await using var connection = await Open();
            await using var transaction = connection.BeginTransaction();
            long instructions = 0;
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => { instructions += 1000; return 0; }, null);
            try
            {
                var method = typeof(SqliteDataStore).GetMethod("PruneDiagnosticBatchAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await (Task)method.Invoke(Store, [connection, transaction, 0, CancellationToken.None])!;
                return instructions;
            }
            finally
            {
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
                await transaction.RollbackAsync();
            }
        }
        public async Task<long> Scalar(string sql)
        {
            await using var connection = await Open(); await using var command = connection.CreateCommand();
            command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        public async ValueTask DisposeAsync()
        {
            await Store.StopDiagnosticsAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(Root, true);
        }
    }
}
