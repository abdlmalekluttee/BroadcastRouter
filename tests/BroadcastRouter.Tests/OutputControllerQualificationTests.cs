using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace BroadcastRouter.Tests;

// CI runs a short exercise. A separately recorded 3600-second local run is
// required by Gate D; the default exercise is not a one-hour qualification.
public sealed class OutputControllerQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentSettingsStartStopAndDisposeWithFaultingChildren()
    {
        var requested = Environment.GetEnvironmentVariable("BROADCASTROUTER_FAKE_QUALIFICATION_SECONDS");
        var seconds = requested is null ? 3 : int.Parse(requested);
        Assert.InRange(seconds, 3, 3600);
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.DeckLinkPortOverrides.Add(new() { StableId = "TEST-OUTPUT", IsOutputPort = true,
            StandbyPresetId = lab.Settings.Presets[0].Id });
        // The control and contenders use real physical-output controller keys,
        // while the fake executable never opens hardware or any URL.
        await lab.Supervisor.StopAsync(lab.Source, CancellationToken.None);
        await lab.Supervisor.StartFallbackAsync(lab.Source, Port("TEST-OUTPUT"), Preset,
            FallbackMode.Black, null, CancellationToken.None);
        var original = lab.Owner;
        var registry = (PortReservationManager)typeof(FfmpegProcessSupervisor)
            .GetField("_outputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab.Supervisor)!;
        var timer = Stopwatch.StartNew();
        var rounds = 0;
        var shutdownRaces = 0;
        var forcedStops = 0;
        var pendingStops = 0;
        var busyRefusals = 0;
        using var ownership = new PhysicalOwners();
        lab.Supervisor.LifecycleChanged += evt =>
        {
            if (evt.Source != lab.Source) ownership.Observe(evt);
            if (evt.State == RouteProcessLifecycleState.ForcedTermination) Interlocked.Increment(ref forcedStops);
        };
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var round = ++rounds;
            await File.WriteAllTextAsync(Path.Combine(lab.Root, "live.json"), JsonSerializer.Serialize(new {
                events = Array.Empty<object>(), ignoreQuit = round % 2 != 0,
                exitAfterMilliseconds = round % 2 == 0 ? (int?)80 : null, exitCode = 37 }));
            var sources = Enumerable.Range(0, 3).Select(index => Source($"round-{round}-{index}")).ToArray();
            var save = Task.Run(async () =>
            {
                var settings = lab.Coordinator.GetSettings();
                settings.Routing.InputReadTimeoutMilliseconds = 1000 + round % 10 * 100;
                await lab.Coordinator.SaveSettingsAsync(settings, "Synthetic qualification");
                typeof(RouterCoordinator).GetMethod("EnsureSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(lab.Coordinator, [settings.MediaTools.FfmpegPath, false]);
                Assert.Same(lab.Supervisor, lab.Get<FfmpegProcessSupervisor>("_supervisor"));
            });
            var route = Task.Run(async () =>
            {
                // Force stop to overlap a launch admitted to the physical
                // controller but not yet published in the process dictionary.
                var gates = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(FfmpegProcessSupervisor)
                    .GetField("_ownerGates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab.Supervisor)!;
                var sourceGate = gates.GetOrAdd(sources[0].Identity.Value, _ => new(1, 1));
                await sourceGate.WaitAsync();
                Task? start = null;
                Task? stop = null;
                try
                {
                    start = Launch(lab.Supervisor, sources[0], 0);
                    await FaultHarnessTests.Eventually(() => registry.ControllerFor("TEST-WORK").Status.OperationActive);
                    stop = lab.Supervisor.StopAsync(sources[0].Identity, CancellationToken.None);
                    Assert.False(stop.IsCompletedSuccessfully, "An admitted launch survived successful stop acknowledgement");
                }
                finally
                {
                    sourceGate.Release();
                    if (start is not null) await start;
                    if (stop is not null) await stop;
                }
                Interlocked.Increment(ref pendingStops);
                Assert.False(lab.Supervisor.TryGetRunning(sources[0].Identity, out _));
                // Different sources and graph purposes contend simultaneously
                // for ONE physical output, not one synthetic port per source.
                await Task.WhenAll(sources.Select((source, index) => Task.Run(async () =>
                {
                    try { await Launch(lab.Supervisor, source, index); }
                    catch (InvalidOperationException ex) { Assert.Contains("owned", ex.Message, StringComparison.OrdinalIgnoreCase); }
                    catch (TimeoutException ex) when (ex.Message == "The physical output controller is busy; ownership was not changed.")
                    { Interlocked.Increment(ref busyRefusals); }
                })));
                await Task.WhenAll(sources.Select(source => lab.Supervisor.StopAsync(source.Identity, CancellationToken.None)));
                await FaultHarnessTests.Eventually(() => registry.ControllerFor("TEST-WORK").Session is null);
            });
            var shutdown = Task.Run(async () =>
            {
                await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture(exitImmediately: true);
                var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromMilliseconds(100));
                using var shutdownOwners = new PhysicalOwners();
                var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
                supervisor.LifecycleChanged += evt => { events.Enqueue(evt); shutdownOwners.Observe(evt); };
                try
                {
                    var attempts = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
                    {
                        try { await Launch(supervisor, Source($"shutdown-{index}"), index % 3); }
                        catch (ObjectDisposedException ex) { Assert.Equal(typeof(FfmpegProcessSupervisor).FullName, ex.ObjectName); }
                        catch (InvalidOperationException ex) { Assert.Contains("owned", ex.Message, StringComparison.OrdinalIgnoreCase); }
                        catch (TimeoutException ex) when (ex.Message == "The physical output controller is busy; ownership was not changed.")
                        { Interlocked.Increment(ref busyRefusals); }
                    })).ToArray();
                    await Task.Yield();
                    await supervisor.DisposeAsync();
                    await Task.WhenAll(attempts);
                    Assert.Empty(supervisor.RunningSnapshot());
                    Assert.All(events.Where(evt => evt.State == RouteProcessLifecycleState.Started), evt =>
                        Assert.Contains(events, final => final.ProcessId == evt.ProcessId && final.State == RouteProcessLifecycleState.Exited));
                    Assert.Empty(shutdownOwners.Failures);
                    Interlocked.Increment(ref shutdownRaces);
                }
                finally { await supervisor.DisposeAsync(); }
            });
            await Task.WhenAll(save, route, shutdown).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(lab.Supervisor.TryGetRunning(lab.Source, out var control));
            Assert.Equal(original.ProcessId, control.ProcessId);
            Assert.Equal(original.SessionId, control.SessionId);
            Assert.DoesNotContain(lab.Events, evt => evt.Source == lab.Source
                && evt.ProcessId == original.ProcessId && evt.State == RouteProcessLifecycleState.StopRequested);
            Assert.Empty(ownership.Failures);
        }
        Assert.True(rounds > 0 && ownership.Starts > 0 && shutdownRaces > 0 && pendingStops > 0);
        output.WriteLine($"Duration={timer.Elapsed}; requested={seconds}s; rounds={rounds}; workerStarts={ownership.Starts}; forcedStops={forcedStops}; shutdownRaces={shutdownRaces}; pendingLaunchStops={pendingStops}; boundedBusyRefusals={busyRefusals}; simultaneousPhysicalOwnerViolations={ownership.Failures.Count}; controlSessionUnchanged=true");
    }

    private static readonly OutputPreset Preset = new("test-mode", "Test mode", new(1920, 1080, 25, 1, "uyvy422"), false, 0);
    private static DeckLinkPort Port(string id) => new(id, id + "-DEVICE", "Synthetic", 0, 0, null, []);
    private static DiscoveredSource Source(string name) => new(new("TEST", "fixture", "default", name), name,
        new Uri("rtsp://example.invalid/fixture"), SourceState.Ready, 0, new("h264", "aac", 1920, 1080, 25, null, 48000, 2, true));
    private static Task Launch(FfmpegProcessSupervisor supervisor, DiscoveredSource source, int purpose) => purpose switch
    {
        0 => supervisor.StartAsync(new(source.Identity, "TEST-WORK", Preset.Id, RouteState.Starting, AssignmentMode.Manual, false),
            source, Port("TEST-WORK"), Preset, CancellationToken.None),
        1 => supervisor.StartFallbackAsync(source.Identity, Port("TEST-WORK"), Preset, FallbackMode.Black, null, CancellationToken.None),
        _ => supervisor.StartPortStandbyAsync(source.Identity, Port("TEST-WORK"), Preset,
            new(StandbyPattern.Black, null, "Synthetic", false), CancellationToken.None)
    };

    // Keep OS handles, not just GUIDs: at every successful launch, all prior
    // writers to this same physical port must already have exited in Windows.
    private sealed class PhysicalOwners : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Process> _owners = [];
        public readonly ConcurrentQueue<string> Failures = new();
        public int Starts { get; private set; }
        public void Observe(RouteProcessLifecycleEvent evt)
        {
            if (evt.State != RouteProcessLifecycleState.Started) return;
            lock (_gate)
            {
                Starts++;
                foreach (var prior in _owners)
                    if (!prior.HasExited) Failures.Enqueue($"Two live children owned TEST-WORK: {prior.Id} and {evt.ProcessId}");
                foreach (var exited in _owners.Where(value => value.HasExited).ToArray())
                { _owners.Remove(exited); exited.Dispose(); }
                try
                {
                    var process = Process.GetProcessById(evt.ProcessId);
                    _ = process.Handle; // retain exact OS identity across PID reuse
                    _owners.Add(process);
                }
                catch (ArgumentException) { } // already exited before observation
            }
        }
        public void Dispose() { foreach (var process in _owners) process.Dispose(); }
    }
}
