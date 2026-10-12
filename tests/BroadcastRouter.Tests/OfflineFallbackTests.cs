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

public sealed class OfflineFallbackTests
{
    [Fact]
    public async Task DueRetryNeverStopsFallbackWithoutFreshMediaProof()
    {
        await using var lab = await RecoveryLab.Create();
        var original = lab.Owner;
        await lab.RestartDue();
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback),
            "R02: an offline retry stopped the working fallback before media was qualified");
        Assert.Equal(original.ProcessId, lab.Owner.ProcessId);
        Assert.Equal(original.SessionId, lab.Owner.SessionId);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    internal sealed class RecoveryLab : IAsyncDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "router-recovery-" + Guid.NewGuid().ToString("N"));
        public SourceIdentity Source { get; } = new("TEST", "fixture", "_default_", "source-A");
        public ManualTimeProvider Time { get; } = new();
        public SupervisionClock Clock { get; }
        public SqliteDataStore Store { get; }
        public RouterCoordinator Coordinator { get; }
        public FfmpegProcessSupervisor Supervisor { get; }
        public System.Collections.Concurrent.ConcurrentQueue<RouteProcessLifecycleEvent> Events { get; } = new();
        public RouteProcessSnapshot Owner => Supervisor.Snapshot().Single(p => p.Source == Source);
        public Dictionary<string, RuntimeRoute> Routes => Get<Dictionary<string, RuntimeRoute>>("_routes");
        public Dictionary<string, DiscoveredSource> Sources => Get<Dictionary<string, DiscoveredSource>>("_sources");
        public OperatorSettings Settings => Get<OperatorSettings>("_settings");
        public RuntimeRoute Route => Routes[Source.Value];

        private RecoveryLab()
        {
            Directory.CreateDirectory(Root);
            Clock = new(Time);
            Store = new(Path.Combine(Root, "test.db"));
            Supervisor = new(new FfmpegRouteOptions("missing-test-executable"), TimeSpan.FromSeconds(1), Clock);
            Coordinator = new(Store, null!, null!, NullLogger<RouterCoordinator>.Instance, Clock);
            Set("_supervisor", Supervisor);
            // Keep the existing supervisor; no real executable can ever be launched.
            Set("_supervisorPath", "missing-test-executable");
            Settings.MediaTools.FfmpegPath = "missing-test-executable";
            Set("_supervisorInputReadTimeoutMilliseconds", Settings.Routing.InputReadTimeoutMilliseconds);
            Settings.SimulationMode = false;
            Supervisor.LifecycleChanged += Events.Enqueue;
        }

        public static async Task<RecoveryLab> Create()
        {
            var lab = new RecoveryLab();
            try
            {
                await lab.Store.InitializeAsync();
                var port = new DeckLinkPort("TEST-OUTPUT", "Synthetic output", "Synthetic model", 0, 0, null,
                    [], true, IsOutputPort: true);
                lab.Get<Dictionary<string, DeckLinkPort>>("_ports")[port.StableId] = port;
                var preset = lab.Settings.Presets.First();
                lab.Sources[lab.Source.Value] = new(lab.Source, "Synthetic source", new Uri("rtsp://example.invalid/fixture"),
                    SourceState.PublisherDisconnected, 0, LastObservedAt: lab.Clock.Now);
                lab.Routes[lab.Source.Value] = new(lab.Source.Value, "Synthetic source", port.StableId, "Synthetic output",
                    preset.Id, RouteState.Fallback, AssignmentMode.Manual, true, 0, 3,
                    null, null, null, 0, 0, null, lab.Clock.Now, "RtspNotFound", "Synthetic offline input",
                    RetryAt: lab.Clock.Now, DesiredPortId: port.StableId);
                lab.Get<PortReservationManager>("_reservations").TryReserve(port.StableId, lab.Source, true, lab.Clock.Now, out _);
                var script = Path.Combine(lab.Root, "child.json");
                await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { events = new[] {
                    new { atMilliseconds = 0, channel = "stdout", text = "frame=1\nout_time_us=40000\nprogress=continue" } } }));
                var start = new ProcessStartInfo(Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg",
                    "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(script);
                await lab.Supervisor.StartOwnedProcessForTestingAsync(lab.Source, start, purpose: RouteProcessPurpose.Fallback);
                await FaultHarnessTests.Eventually(() => lab.Owner.Progress?.Frame > 0);
                return lab;
            }
            catch { await lab.DisposeAsync(); throw; }
        }

        public Task RestartDue() => Invoke("RestartReservedRouteAsync", Route, CancellationToken.None, false);
        public Task Invoke(string method, params object?[] args) => (Task)typeof(RouterCoordinator).GetMethod(method, Fields)!.Invoke(Coordinator, args)!;
        public T Get<T>(string name) => (T)typeof(RouterCoordinator).GetField(name, Fields)!.GetValue(Coordinator)!;
        public void Set(string name, object value) => typeof(RouterCoordinator).GetField(name, Fields)!.SetValue(Coordinator, value);
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            Coordinator.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }
}
