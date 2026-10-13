using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class CoordinatorIndependentMediaTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HealthyMediaRetainsOwnerDespiteSustainedDuplicateCounts(bool fastLoop, bool hold)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await Lab.Create(fixture, true, captureTelemetry: true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        var source = fixture.Source.Identity.Value;
        lab.Routes[source] = lab.Route with { State = RouteState.Running, HoldOutputWhilePublisherLive = hold };
        lab.Settings.WowzaServers.Add(new() { ServerId = "TEST", Enabled = true });
        var session = lab.Owner.SessionId;
        for (var i = 0; i <= 5; i++)
        {
            if (i > 0) await lab.Qualify();
            await lab.Progress(100 + i * 25, i * 25);
            lab.Get<PublisherLiveHoldPolicy>("_publisherLiveHold").Observe(source, true);
            if (fastLoop) await lab.FastInputTurn(100 + i * 25); else await lab.Monitor();
            Assert.True(lab.Owner.Running);
            Assert.Equal(session, lab.Owner.SessionId);
            Assert.Equal(0, lab.Route.RestartCount);
            Assert.All(lab.Owner.IndependentMedia!.Signals, s => Assert.Equal(MediaSignalState.Healthy, s.State));
        }
        await lab.Telemetry!.FlushAsync();
        var decisions = lab.Decisions();
        Assert.DoesNotContain(decisions, d => d.GetProperty("Action").GetString() is "StopOwnedProcess" or "StopForOutputHandoff");
        if (fastLoop && !hold) Assert.Contains(decisions, d => d.GetProperty("Action").GetString() == "KeepOwner"
            && d.GetProperty("Result").GetString() == "InsufficientMediaEvidence");
        if (fastLoop && hold) Assert.Contains(decisions, d => d.GetProperty("Action").GetString() == "KeepOwner"
            && d.GetProperty("Result").GetString() == "RecoverySuppressed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyDeadMediaRecoversAfterStartupEvenWithPublisherHold(bool fastLoop)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await Lab.Create(fixture, true, captureTelemetry: true);
        for (var i = 0; i <= 2; i++)
        {
            if (i > 0) lab.Time.Advance(TimeSpan.FromMilliseconds(500));
            await FaultHarnessTests.Eventually(() => lab.Owner.IndependentMedia!.Signals.All(s => s.LastAdvancedAt == lab.Clock.Now));
        }
        await FaultHarnessTests.Eventually(() => lab.Owner.RecentErrors.Any(e => e.Contains("Synthetic observation batch complete")));
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        lab.Routes[fixture.Source.Identity.Value] = lab.Route with { HoldOutputWhilePublisherLive = true };
        lab.Settings.WowzaServers.Add(new() { ServerId = "TEST", Enabled = true });
        lab.Get<PublisherLiveHoldPolicy>("_publisherLiveHold").Observe(fixture.Source.Identity.Value, true);
        if (fastLoop) await lab.FastInputTurn(); else await lab.Monitor();
        Assert.False(lab.Owner.Running, "Valid early-stalled media remained alive indefinitely under publisher hold.");
        Assert.Equal(1, lab.Route.RestartCount);
        Assert.Equal("MediaAdvancementLost", lab.Route.FailureCategory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneFailedMediaSignalWithFreshProgressRetainsOwnerAndRecordsAbstention(bool fastLoop)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture(stopPcmAfterBatch: 30);
        await using var lab = await Lab.Create(fixture, true, captureTelemetry: true);
        await lab.Qualify();
        await FaultHarnessTests.Eventually(() => lab.Owner.RecentErrors.Any(e => e.Contains("Synthetic audio observation batch complete")));
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        for (var i = 0; i <= 4; i++)
        {
            if (i > 0) lab.Time.Advance(TimeSpan.FromMilliseconds(500));
            await FaultHarnessTests.Eventually(() => lab.Owner.IndependentMedia!.Signals
                .Where(s => s.Kind != MediaSignalKind.InputAudio).All(s => s.LastAdvancedAt == lab.Clock.Now));
        }
        await lab.Progress(1000);
        var signals = lab.Owner.IndependentMedia!.Signals;
        Assert.Equal(MediaSignalState.Failed, signals.Single(s => s.Kind == MediaSignalKind.InputAudio).State);
        Assert.All(signals.Where(s => s.Kind != MediaSignalKind.InputAudio), s => Assert.Equal(MediaSignalState.Healthy, s.State));
        Assert.Null(lab.Owner.InputFailure);
        var session = lab.Owner.SessionId;
        if (fastLoop) await lab.FastInputTurn(); else await lab.Monitor();
        Assert.True(lab.Owner.Running);
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.Equal(0, lab.Route.RestartCount);
        await lab.Telemetry!.FlushAsync();
        var decision = Assert.Single(lab.Decisions(), value => value.GetProperty("Action").GetString() == "KeepOwner"
            && value.GetProperty("Result").GetString() == "InsufficientMediaEvidence");
        Assert.Single(decision.GetProperty("Before").GetProperty("IndependentMedia").GetProperty("Signals")
            .EnumerateArray(), s => s.GetProperty("State").GetString() == "Failed");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ExplicitManualSourceDisableOrRemovalStopsOwner(bool saved, bool removed)
    {
        var identity = new SourceIdentity("MANUAL", "manual", "_definst_", "configured-source");
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture(identity: identity);
        await using var lab = await Lab.Create(fixture, true, captureTelemetry: true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        await lab.Progress(1000);
        if (!removed) lab.Settings.ManualSources.Add(new() { StableId = "configured-source", Enabled = false,
            FriendlyName = "Synthetic", RtspUrl = "rtsp://example.invalid/fixture" });
        lab.Get<Dictionary<string, DiscoveredSource>>("_sources")[identity.Value] = fixture.Source with
            { State = removed ? SourceState.PublisherDisconnected : SourceState.Disabled };
        lab.Get<Dictionary<string, DeckLinkPort>>("_ports")["TEST-OUTPUT"] =
            new("TEST-OUTPUT", "Synthetic", "Synthetic", 0, 0, null, [], true, IsOutputPort: true);
        lab.Get<PortReservationManager>("_reservations").TryReserve("TEST-OUTPUT", identity, false, lab.Clock.Now, out _);
        lab.Routes[identity.Value] = lab.Route with { State = RouteState.Running, PortId = "TEST-OUTPUT",
            DesiredPortId = saved ? "TEST-OUTPUT" : null, AssignmentMode = saved ? AssignmentMode.Manual : AssignmentMode.Automatic };
        lab.Get<Dictionary<string, DateTimeOffset>>("_sourceMissingSince")[identity.Value] = lab.Clock.Now.AddMinutes(-5);
        if (saved) await lab.Invoke("ReconcileSavedRouteAsync", identity.Value, CancellationToken.None);
        else await lab.Invoke("ReleaseExpiredMissingRoutesAsync", lab.Settings, CancellationToken.None);
        Assert.False(lab.Owner.Running, "Explicit operator source disable/removal was ignored by media preservation.");
        Assert.Equal(saved ? RouteState.WaitingForStream : RouteState.Released, lab.Route.State);
        Assert.Equal(0, lab.Route.RestartCount);
    }

    [Theory]
    [InlineData("saved-source")]
    [InlineData("expired-source")]
    [InlineData("media-mode")]
    public async Task CachedSourceOrProbeChangesCannotStopAHealthyOwnedSession(string path)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await Lab.Create(fixture, true, captureTelemetry: true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        var sourceId = fixture.Source.Identity.Value;
        var session = lab.Owner.SessionId;
        lab.Get<Dictionary<string, DeckLinkPort>>("_ports")["TEST-OUTPUT"] =
            new("TEST-OUTPUT", "Synthetic", "Synthetic", 0, 0, null, [], true, IsOutputPort: true);
        lab.Get<PortReservationManager>("_reservations").TryReserve("TEST-OUTPUT", fixture.Source.Identity,
            false, lab.Clock.Now, out _);
        lab.Routes[sourceId] = lab.Route with { State = RouteState.Running, PortId = "TEST-OUTPUT",
            DesiredPortId = "TEST-OUTPUT", AssignmentMode = AssignmentMode.Manual };
        Exception? error;
        if (path == "media-mode")
        {
            lab.Get<HashSet<string>>("_pendingMediaModeRestarts").Add(sourceId);
            error = await Record.ExceptionAsync(() => lab.Invoke("RestartRoutesForMediaModeChangesAsync", CancellationToken.None));
        }
        else
        {
            lab.Get<Dictionary<string, DiscoveredSource>>("_sources")[sourceId] = fixture.Source with
                { State = SourceState.RtspUnavailable, Media = null };
            lab.Get<Dictionary<string, DateTimeOffset>>("_sourceMissingSince")[sourceId] = lab.Clock.Now.AddMinutes(-5);
            error = await Record.ExceptionAsync(() => path == "saved-source"
                ? lab.Invoke("ReconcileSavedRouteAsync", sourceId, CancellationToken.None)
                : lab.Invoke("ReleaseExpiredMissingRoutesAsync", lab.Settings, CancellationToken.None));
        }
        Assert.True(lab.Owner.Running, "A cached source/probe observation stopped healthy owned media.");
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.Equal(RouteState.Running, lab.Route.State);
        Assert.Equal(0, lab.Route.RestartCount);
        Assert.Null(error);
        await lab.Telemetry!.FlushAsync();
        Assert.Contains(lab.Decisions(), value => value.GetProperty("Action").GetString() == "KeepOwner"
            && value.GetProperty("Result").GetString() == "InsufficientMediaEvidence");
    }

    [Fact]
    public async Task HealthyIndependentMediaSurvivesAnExpiredFirstProgressDeadline()
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await Lab.Create(fixture,true);
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        await lab.Qualify();
        Assert.Null(lab.Owner.Progress);
        Assert.All(lab.Owner.IndependentMedia!.Signals,s=>Assert.Equal(MediaSignalState.Healthy,s.State));
        var owner = lab.Owner.SessionId;
        await lab.Monitor();
        Assert.True(lab.Owner.Running,"Healthy media was stopped because control progress was missing.");
        Assert.Equal(owner,lab.Owner.SessionId);
        Assert.Equal(0,lab.Route.RestartCount);
        Assert.Equal(RouteState.Running,lab.Route.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StderrWithoutCorroboratingMediaDoesNotAuthorizeAStop(bool fastLoop)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture(stderr:"[in#0/rtsp] CSeq 96 expected, 0 received.");
        await using var lab = await Lab.Create(fixture,false, captureTelemetry: true);
        await FaultHarnessTests.Eventually(()=>lab.Owner.InputFailure is not null);
        Assert.Null(lab.Owner.IndependentMedia);
        if (fastLoop) await lab.FastInputTurn(); else await lab.Monitor();
        Assert.True(lab.Owner.Running,"A stderr warning stopped an owner without independent media evidence.");
        Assert.Equal(0,lab.Route.RestartCount);
        await lab.Monitor(); // Repeated observations are not new decisions or a log flood.
        await lab.Telemetry!.FlushAsync();
        var decisions = lab.Decisions();
        Assert.Single(decisions, value => value.GetProperty("Action").GetString() == "KeepOwner"
            && value.GetProperty("Result").GetString() == "InsufficientMediaEvidence");
        Assert.DoesNotContain(decisions, value => value.GetProperty("Action").GetString() is "StopOwnedProcess" or "StopForOutputHandoff");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedMediaLossRecoversEvenWhenPublisherHoldIsEnabled(bool fastLoop)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var lab = await Lab.Create(fixture,true, captureTelemetry: true);
        await lab.Qualify();
        await FaultHarnessTests.Eventually(()=>lab.Owner.RecentErrors.Any(e=>e.Contains("Synthetic observation batch complete")));
        lab.Time.Advance(TimeSpan.FromSeconds(25));
        lab.Routes[fixture.Source.Identity.Value] = lab.Route with { HoldOutputWhilePublisherLive=true };
        lab.Settings.WowzaServers.Add(new() { ServerId="TEST",Enabled=true });
        lab.Get<PublisherLiveHoldPolicy>("_publisherLiveHold").Observe(fixture.Source.Identity.Value,true);
        Assert.True(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
        if (fastLoop) await lab.FastInputTurn(); else await lab.Monitor();
        Assert.False(lab.Owner.Running);
        Assert.Equal("MediaAdvancementLost",lab.Route.FailureCategory);
        Assert.Equal(1,lab.Route.RestartCount);
        await lab.Telemetry!.FlushAsync();
        var stop = Assert.Single(lab.Decisions(), value => value.GetProperty("Trigger").GetString() == "MediaAdvancementLost"
            && value.GetProperty("Action").GetString() == "StopForOutputHandoff"
            && value.GetProperty("Result").GetString() == "Requested");
        var evidence = stop.GetProperty("Before").GetProperty("IndependentMedia");
        Assert.True(evidence.GetProperty("ConfirmedMediaFailure").GetBoolean());
        Assert.Equal(4, evidence.GetProperty("Signals").GetArrayLength());
        Assert.All(evidence.GetProperty("Signals").EnumerateArray(), signal => Assert.Equal("Failed", signal.GetProperty("State").GetString()));
    }

    private sealed class Lab : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance|BindingFlags.NonPublic;
        private readonly SupervisorMediaEvidenceTests.MediaChildFixture _fixture;
        internal ManualTimeProvider Time { get; } = new();
        internal SupervisionClock Clock { get; }
        internal RouterCoordinator Coordinator { get; }
        internal FfmpegProcessSupervisor Supervisor { get; }
        internal SupervisionTelemetry? Telemetry { get; }
        private SqliteDataStore Store { get; }
        internal Dictionary<string,RuntimeRoute> Routes => Get<Dictionary<string,RuntimeRoute>>("_routes");
        internal OperatorSettings Settings => Get<OperatorSettings>("_settings");
        internal RuntimeRoute Route => Routes[_fixture.Source.Identity.Value];
        internal RouteProcessSnapshot Owner => Assert.Single(Supervisor.Snapshot());
        private Lab(SupervisorMediaEvidenceTests.MediaChildFixture fixture,bool instrumented, bool captureTelemetry)
        {
            _fixture=fixture;
            Clock=new(Time);
            Store=new(Path.Combine(fixture.Root,"test.db"));
            if (captureTelemetry) Telemetry = new(Path.Combine(fixture.Root,"logs"),Clock, readVolumeSpace: TelemetryTestVolume.Read);
            Supervisor=new(new(fixture.Executable,UseIndependentMediaStats:instrumented),TimeSpan.FromSeconds(1),Clock,Telemetry);
            Coordinator=new(Store,null!,null!,NullLogger<RouterCoordinator>.Instance,Clock,Telemetry);
            typeof(RouterCoordinator).GetField("_supervisor",Flags)!.SetValue(Coordinator,Supervisor);
            Get<Dictionary<string,DiscoveredSource>>("_sources")[fixture.Source.Identity.Value]=fixture.Source;
            Routes[fixture.Source.Identity.Value]=new(fixture.Source.Identity.Value,"Synthetic",null,null,"1080i50",
                RouteState.Starting,AssignmentMode.Automatic,false,0,0,null,null,null,0,0,Clock.Now,Clock.Now,null,null);
            Settings.SimulationMode=false;
        }
        internal static async Task<Lab> Create(SupervisorMediaEvidenceTests.MediaChildFixture fixture,bool instrumented, bool captureTelemetry = false)
        {
            var lab=new Lab(fixture,instrumented,captureTelemetry);
            try { await lab.Store.InitializeAsync(); await fixture.Start(lab.Supervisor); return lab; }
            catch { await lab.DisposeAsync(); throw; }
        }
        internal async Task Qualify()
        {
            for (var i=0;i<=4;i++)
            {
                if(i>0)Time.Advance(TimeSpan.FromMilliseconds(500));
                await FaultHarnessTests.Eventually(()=>Owner.IndependentMedia!.Signals.All(s=>s.LastAdvancedAt==Clock.Now));
            }
        }
        internal Task Monitor()=>(Task)typeof(RouterCoordinator).GetMethod("MonitorProcessesAsync",Flags)!.Invoke(Coordinator,[CancellationToken.None])!;
        internal async Task Progress(long frame, long? duplicates = null)
        {
            var owners = typeof(FfmpegProcessSupervisor).GetField("_running", Flags)!.GetValue(Supervisor)!;
            var managed = owners.GetType().GetProperty("Item")!.GetValue(owners, [_fixture.Source.Identity.Value])!;
            var process = (Process)managed.GetType().GetProperty("Process")!.GetValue(managed)!;
            await process.StandardInput.WriteLineAsync(duplicates is { } count
                ? $"progress-duplicates {frame} {frame * 40000} {count}"
                : $"progress {frame} {frame * 40000}");
            await process.StandardInput.FlushAsync();
            await FaultHarnessTests.Eventually(() => Owner.Progress?.Frame == frame);
        }
        internal Task Invoke(string method, params object[] arguments) =>
            (Task)typeof(RouterCoordinator).GetMethod(method, Flags)!.Invoke(Coordinator, arguments)!;
        internal JsonElement[] Decisions() => Directory.GetFiles(Path.Combine(_fixture.Root,"logs"),"*.decisions.*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line =>
            { using var document = JsonDocument.Parse(line); return document.RootElement.GetProperty("Event").Clone(); }).ToArray();
        internal async Task FastInputTurn(long? expectedDuplicateFrame = null)
        {
            using var stop = new CancellationTokenSource();
            var task = (Task)typeof(RouterCoordinator).GetMethod("RunFastInputSupervisionAsync",Flags)!.Invoke(Coordinator,[stop.Token])!;
            try
            {
                Time.Advance(TimeSpan.FromMilliseconds(100));
                // Waiting on the same per-route gate proves the first iteration
                // reached and finished its owned-session decision (not a sleep).
                var gates = Get<System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim>>("_routeGates");
                await FaultHarnessTests.Eventually(()=>gates.ContainsKey(_fixture.Source.Identity.Value));
                if (expectedDuplicateFrame is { } expected)
                    await FaultHarnessTests.Eventually(() =>
                    {
                        lock (Get<object>("_gate"))
                            return Get<Dictionary<string, FfmpegInputFreezeDetector>>("_inputFreezeDetectors")
                                .TryGetValue(_fixture.Source.Identity.Value, out var detector)
                                && (long)typeof(FfmpegInputFreezeDetector).GetField("_frame", Flags)!.GetValue(detector)! == expected;
                    });
                await gates[_fixture.Source.Identity.Value].WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                gates[_fixture.Source.Identity.Value].Release();
            }
            finally { stop.Cancel(); try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch(OperationCanceledException) when(stop.IsCancellationRequested) { } }
        }
        internal T Get<T>(string name)=>(T)typeof(RouterCoordinator).GetField(name,Flags)!.GetValue(Coordinator)!;
        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            Coordinator.Dispose();
            if (Telemetry is not null) await Telemetry.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
