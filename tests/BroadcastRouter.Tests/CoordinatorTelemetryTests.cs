using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class CoordinatorTelemetryTests
{
    [Fact]
    public async Task ExitedChildIsLinkedToActualCoordinatorRetryAndExactChosenDelay()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-coordinator-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteDataStore? store = null;
        try
        {
            var clock = new SupervisionClock(new ManualTimeProvider());
            store = new SqliteDataStore(Path.Combine(root, "test.db"), readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
            await store.InitializeAsync();
            await using var telemetry = new SupervisionTelemetry(Path.Combine(root, "logs"), clock, readVolumeSpace: TelemetryTestVolume.Read);
            await using var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused"), TimeSpan.FromSeconds(1), clock, telemetry);
            using var coordinator = new RouterCoordinator(store, null!, null!, NullLogger<RouterCoordinator>.Instance, clock, telemetry);
            var source = new SourceIdentity("TEST", "fixture", "_default_", "source-A");
            var route = new RuntimeRoute(source.Value, "Synthetic source", null, null, "1080p25", RouteState.Running,
                AssignmentMode.Automatic, false, 0, 0, 1, 25, 1, 0, 0, clock.Now, clock.Now, null, null);
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(RouterCoordinator).GetField("_supervisor", fields)!.SetValue(coordinator, supervisor);
            var routes = (Dictionary<string, RuntimeRoute>)typeof(RouterCoordinator).GetField("_routes", fields)!.GetValue(coordinator)!;
            routes[source.Value] = route;
            var settings = (OperatorSettings)typeof(RouterCoordinator).GetField("_settings", fields)!.GetValue(coordinator)!;
            settings.SimulationMode = false;
            var script = Path.Combine(root, "child.json");
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
            {
                events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "synthetic end-of-session" } },
                exitAfterMilliseconds = 50, exitCode = FfmpegErrorClassifier.AvErrorHttpNotFound
            }));
            var start = new ProcessStartInfo(Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            telemetry.ObservePublisher(source.Value, true);
            await supervisor.StartOwnedProcessForTestingAsync(source, start);
            await FaultHarnessTests.Eventually(() => supervisor.Snapshot().Single().ExitCode is not null);
            var owner = supervisor.Snapshot().Single();
            await (Task)typeof(RouterCoordinator).GetMethod("MonitorProcessesAsync", fields)!.Invoke(coordinator, [CancellationToken.None])!;
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var documents = Directory.GetFiles(Path.Combine(root, "logs"), "*.decisions.*.jsonl")
                .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var events = documents.Select(document => document.RootElement.GetProperty("Event")).ToArray();
                Assert.Contains(events, value => value.GetProperty("Trigger").GetString() == "PublisherObservation");
                Assert.Contains(events, value => value.GetProperty("Action").GetString() == "Exited"
                    && value.GetProperty("Result").GetString() == "Exited: AVERROR_HTTP_NOT_FOUND");
                var retry = events.Single(value => value.GetProperty("Result").GetString() == "RetryScheduled");
                Assert.Equal("RtspNotFound", retry.GetProperty("Trigger").GetString());
                Assert.Equal(owner.SessionId, retry.GetProperty("SessionId").GetGuid());
                Assert.Equal(owner.ProcessId, retry.GetProperty("ProcessId").GetInt32());
                Assert.Equal(owner.ProcessStartUtc, retry.GetProperty("ProcessStartUtc").GetDateTimeOffset());
                Assert.Equal(1, retry.GetProperty("SourceEpoch").GetInt64());
                Assert.Equal(RouteState.Reconnecting, routes[source.Value].State);
                Assert.Equal((routes[source.Value].RetryAt!.Value - clock.Now).TotalMilliseconds,
                    retry.GetProperty("WindowMilliseconds").GetDouble(), 3);
                Assert.Contains(events, value => value.GetProperty("Result").GetString() == "PersistedAndApplied");
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "logs"), owner.StderrLog!));
            }
            finally { foreach (var document in documents) document.Dispose(); }
        }
        finally
        {
            if (store is not null) await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }
}
