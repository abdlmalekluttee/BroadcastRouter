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

    [Fact]
    public async Task TwentyFourHoursOfflineKeepsExactFallbackOwner()
    {
        await using var lab = await RecoveryLab.Create();
        var original = lab.Owner;
        for (var second = 0; second < 86400; second++)
        {
            await lab.RestartDue();
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.Equal(original.SessionId, lab.Owner.SessionId);
        Assert.Equal(1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task RealProbeQualificationProducesExactlyOneSerializedHandoff()
    {
        await using var lab = await RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        var old = lab.Owner;
        await lab.RestartDue();
        for (var i = 0; i < 100 && !lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live); i++)
        {
            await Task.Delay(20);
            await lab.RestartDue();
        }
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live),
            "R02: a fresh decoded AV probe did not produce a live handoff");
        await FaultHarnessTests.Eventually(() => lab.Owner.Progress?.Frame > 0);
        for (var i = 0; i < 20; i++) await lab.RestartDue();
        var events = lab.Events.ToArray();
        Assert.Equal(2, events.Count(e => e.State == RouteProcessLifecycleState.Started));
        Assert.Equal(1, events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
        Assert.True(Array.FindIndex(events, e => e.State == RouteProcessLifecycleState.Exited && e.ProcessId == old.ProcessId)
            < Array.FindIndex(events, e => e.State == RouteProcessLifecycleState.Started && e.ProcessId != old.ProcessId));
        Assert.NotEqual(old.SessionId, lab.Owner.SessionId);
    }

    [Fact]
    public async Task StaleMediaModeRequestCannotBypassReadiness()
    {
        await using var lab = await RecoveryLab.Create();
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { State = SourceState.Ready, Media = RecoveryReadinessTests.Av().Media };
        lab.Get<HashSet<string>>("_pendingMediaModeRestarts").Add(lab.Source.Value);
        await lab.Invoke("RestartRoutesForMediaModeChangesAsync", CancellationToken.None);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task AutomaticRouteDiscoveryStillProbesAndCanRecover()
    {
        await using var lab = await RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Routes[lab.Source.Value] = lab.Route with { AssignmentMode = AssignmentMode.Automatic, DesiredPortId = null };
        var observation = lab.Sources[lab.Source.Value] with { State = SourceState.PublisherActive };
        var task = lab.Invoke("ProbeObservationAsync", observation, lab.Settings, CancellationToken.None, null);
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var source = (DiscoveredSource)result.GetType().GetProperty("Source")!.GetValue(result)!;
        Assert.Equal(SourceState.Ready, source.State);
        lab.Sources[lab.Source.Value] = source;
        Assert.True(RapidStreamRecoveryPolicy.CanAttemptReservedRecovery(lab.Route, source));
        for (var i = 0; i < 100 && !lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live); i++)
        { await lab.RestartDue(); await Task.Delay(20); }
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
    }

    [Fact]
    public async Task DiscoveryDoesNotBypassRecoveryProbeBackoff()
    {
        await using var lab = await RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        var known = RecoveryReadinessTests.Av().Media;
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { Media = known };
        var observation = lab.Sources[lab.Source.Value] with { State = SourceState.PublisherActive, Media = null };
        var task = lab.Invoke("ProbeObservationAsync", observation, lab.Settings, CancellationToken.None, null);
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Null(result.GetType().GetProperty("Probe")!.GetValue(result));
        var source = (DiscoveredSource)result.GetType().GetProperty("Source")!.GetValue(result)!;
        Assert.Equal(SourceState.PublisherActive, source.State);
        Assert.Equal(known, source.Media);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOutputOrToolsNeverStopsFallback(bool disablePort)
    {
        await using var lab = await RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        if (disablePort)
        {
            var ports = lab.Get<Dictionary<string, DeckLinkPort>>("_ports");
            ports["TEST-OUTPUT"] = ports["TEST-OUTPUT"] with { IsOutputPort = false };
        }
        else lab.Set("_validation", new MediaToolValidation(ToolValidationState.Invalid, "fake", "fake", false, false, 0, [], lab.Clock.Now));
        for (var i = 0; i < 30; i++) { await lab.RestartDue(); await Task.Delay(20); }
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("settings-revision")]
    [InlineData("simulation-mode")]
    [InlineData("device-rediscovery")]
    public async Task ChangeWhileWaitingForPortGateKeepsFallbackUntouched(string change)
    {
        await using var lab = await RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        var portGate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>("_portGates")
            .GetOrAdd("TEST-OUTPUT", _ => new(1, 1));
        await portGate.WaitAsync();
        var attempt = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++) { await lab.RestartDue(); await Task.Delay(20); }
        });
        try
        {
            await FaultHarnessTests.Eventually(() => lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")
                .TryGetValue(lab.Source.Value, out var gate) && (DateTimeOffset)typeof(RecoveryReadinessGate)
                    .GetField("_nextProbe", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate)! >= lab.Clock.Now.AddSeconds(30));
            // The proof has been consumed; the handoff must wait for the port
            // semaphore without stopping the current writer.
            Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
            switch (change)
            {
                case "publisher": lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")[lab.Source.Value].Invalidate(); break;
                case "settings-revision": lab.Settings.ConfigurationRevision++; break;
                case "simulation-mode": lab.Settings.SimulationMode = true; break;
                case "device-rediscovery":
                    var ports = lab.Get<Dictionary<string, DeckLinkPort>>("_ports");
                    ports["TEST-OUTPUT"] = ports["TEST-OUTPUT"] with { FfmpegName = "Remapped output" };
                    break;
            }
        }
        finally { portGate.Release(); await attempt; }
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
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
        public SupervisionTelemetry? Telemetry { get; }
        private Microsoft.Extensions.Logging.LoggerFactory? _loggerFactory;
        public System.Collections.Concurrent.ConcurrentQueue<RouteProcessLifecycleEvent> Events { get; } = new();
        public RouteProcessSnapshot Owner => Supervisor.Snapshot().Single(p => p.Source == Source);
        public Dictionary<string, RuntimeRoute> Routes => Get<Dictionary<string, RuntimeRoute>>("_routes");
        public Dictionary<string, DiscoveredSource> Sources => Get<Dictionary<string, DiscoveredSource>>("_sources");
        public OperatorSettings Settings => Get<OperatorSettings>("_settings");
        public RuntimeRoute Route => Routes[Source.Value];

        private RecoveryLab(IHttpClientFactory? http = null, bool captureTelemetry = false)
        {
            Directory.CreateDirectory(Root);
            Clock = new(Time);
            if (captureTelemetry) Telemetry = new(Path.Combine(Root, "telemetry"), Clock, readVolumeSpace: TelemetryTestVolume.Read);
            Store = new(Path.Combine(Root, "test.db"));
            var executable = Path.Combine(Root, "BroadcastRouter.FakeFfmpeg.exe");
            Supervisor = new(new FfmpegRouteOptions(executable), TimeSpan.FromSeconds(1), Clock, Telemetry);
            if (Telemetry is not null) _loggerFactory = new([new RouterFileLoggerProvider(Telemetry)]);
            Microsoft.Extensions.Logging.ILogger<RouterCoordinator> logger = _loggerFactory is null
                ? NullLogger<RouterCoordinator>.Instance : new Microsoft.Extensions.Logging.Logger<RouterCoordinator>(_loggerFactory);
            Coordinator = new(Store, http!, null!, logger, Clock, Telemetry);
            Set("_supervisor", Supervisor);
            // Keep the existing supervisor; no real executable can ever be launched.
            Set("_supervisorPath", executable);
            Settings.MediaTools.FfmpegPath = executable;
            Set("_supervisorInputReadTimeoutMilliseconds", Settings.Routing.InputReadTimeoutMilliseconds);
            Settings.SimulationMode = false;
            Supervisor.LifecycleChanged += Events.Enqueue;
        }

        public static async Task<RecoveryLab> Create(IHttpClientFactory? http = null, bool captureTelemetry = false)
        {
            var lab = new RecoveryLab(http, captureTelemetry);
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
                await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { progressCommands = captureTelemetry, events = new[] {
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
        public async Task InstallFakeMediaTools()
        {
            var bin = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
            foreach (var file in Directory.GetFiles(bin)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            var executable = Path.Combine(Root, "BroadcastRouter.FakeFfmpeg.exe");
            Settings.MediaTools.FfmpegPath = executable;
            Settings.MediaTools.FfprobePath = executable;
            Set("_supervisorPath", executable);
            Set("_validation", new MediaToolValidation(ToolValidationState.Valid, "fake", "fake", true, true, 1, [], Clock.Now));
            await File.WriteAllTextAsync(Path.Combine(Root, "probe.json"), JsonSerializer.Serialize(new
            {
                events = new[] { new { atMilliseconds = 0, channel = "stdout", text = """
                  {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1","nb_read_frames":"50"},
                  {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_frames":"94"}]}
                  """ } }, exitAfterMilliseconds = 20
            }));
            await File.WriteAllTextAsync(Path.Combine(Root, "live.json"), JsonSerializer.Serialize(new
            { events = new[] { new { atMilliseconds = 0, channel = "stdout", text = "frame=1\nout_time_us=40000\nprogress=continue" } } }));
        }
        public Task Invoke(string method, params object?[] args) => (Task)typeof(RouterCoordinator).GetMethod(method, Fields)!.Invoke(Coordinator, args)!;
        public T Get<T>(string name) => (T)typeof(RouterCoordinator).GetField(name, Fields)!.GetValue(Coordinator)!;
        public void Set(string name, object value) => typeof(RouterCoordinator).GetField(name, Fields)!.SetValue(Coordinator, value);
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            Coordinator.Dispose();
            _loggerFactory?.Dispose();
            if (Telemetry is not null) await Telemetry.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await FixtureDirectory.DeleteAsync(Root);
        }
    }
}
