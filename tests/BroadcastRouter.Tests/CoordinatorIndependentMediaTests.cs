using System.Reflection;
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
        Assert.Single(decisions.Where(value => value.GetProperty("Action").GetString() == "KeepOwner"
            && value.GetProperty("Result").GetString() == "InsufficientMediaEvidence"));
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
        var stop = Assert.Single(lab.Decisions().Where(value => value.GetProperty("Trigger").GetString() == "MediaAdvancementLost"
            && value.GetProperty("Action").GetString() == "StopForOutputHandoff"
            && value.GetProperty("Result").GetString() == "Requested"));
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
        internal Task Invoke(string method, params object[] arguments) =>
            (Task)typeof(RouterCoordinator).GetMethod(method, Flags)!.Invoke(Coordinator, arguments)!;
        internal JsonElement[] Decisions() => Directory.GetFiles(Path.Combine(_fixture.Root,"logs"),"*.decisions.*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line =>
            { using var document = JsonDocument.Parse(line); return document.RootElement.GetProperty("Event").Clone(); }).ToArray();
        internal async Task FastInputTurn()
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
