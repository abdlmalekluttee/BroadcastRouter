using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DeclaredMediaCoordinatorTests
{
    [Fact]
    public async Task InitialStartWaitsThenHandsOffOnceWhenRequiredTracksQualify()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var standby = await PrepareInitialStart(lab);
        await StartInitial(lab);
        Assert.Equal(RouteState.WaitingForStream, lab.Route.State);
        Assert.True(lab.Supervisor.IsRunning(standby, RouteProcessPurpose.Fallback));
        await AwaitPending(lab);
        await lab.Invoke("ReconcileSavedRouteAsync", lab.Source.Value, CancellationToken.None);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        Assert.False(lab.Supervisor.IsRunning(standby, RouteProcessPurpose.Fallback));
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.Started);
    }

    [Fact]
    public async Task InitialRequiredAudioWithMutedPresetRetainsPortStandby()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var standby = await PrepareInitialStart(lab);
        lab.Settings.SourceMediaProfiles.Add(new() { SourceId = lab.Source.Value, RequireAudio = true });
        lab.Settings.Presets[0].IncludeAudio = false;
        await StartInitial(lab);
        Assert.True(lab.Supervisor.IsRunning(standby, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task InitialDeclarationChangedWhileWaitingForPortGateRetainsStandby()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var standby = await PrepareInitialStart(lab);
        // Warm the asynchronous proof without claiming a handoff.
        typeof(BroadcastRouter.Web.Services.RouterCoordinator).GetMethod("PollRecoveryReadiness", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lab.Coordinator, [lab.Route, lab.Sources[lab.Source.Value], lab.Settings.Presets[0], CancellationToken.None]);
        await AwaitPending(lab);
        var gate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>("_portGates")
            .GetOrAdd("TEST-OUTPUT", _ => new(1, 1));
        await gate.WaitAsync();
        var attempt = StartInitial(lab);
        try
        {
            Assert.False(attempt.IsCompleted);
            lab.Settings.SourceMediaProfiles.Add(new() { SourceId = lab.Source.Value, Expectation = SourceMediaExpectation.AudioOnly });
        }
        finally { gate.Release(); await attempt; }
        Assert.True(lab.Supervisor.IsRunning(standby, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task ManualRestoreWithUnknownMediaRetainsExistingFallback()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        await WriteProbe(lab, "{}");
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { State = SourceState.Unknown };
        var owner = lab.Owner.SessionId;
        await lab.Invoke("RestartReservedRouteAsync", lab.Route, CancellationToken.None, true);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"streams\":[null]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"nb_read_frames\":\"0\"}]}")]
    public async Task InconclusiveDiscoveryCannotExpireAutomaticFallback(string probe)
    {
        using var http = new ConnectedInventory();
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create(http);
        await lab.InstallFakeMediaTools();
        lab.Settings.WowzaServers.Add(new() { ServerId = "TEST", Applications = "fixture", ApplicationInstances = "_default_",
            ManagementUrl = "http://example.invalid/", ConnectionTimeoutSeconds = 8 });
        lab.Routes[lab.Source.Value] = lab.Route with { AssignmentMode = AssignmentMode.Automatic, DesiredPortId = null, Locked = false };
        var reservations = lab.Get<PortReservationManager>("_reservations");
        Assert.True(reservations.Release("TEST-OUTPUT", lab.Source, force: true));
        Assert.True(reservations.TryReserve("TEST-OUTPUT", lab.Source, false, lab.Clock.Now, out _));
        lab.Get<Dictionary<string, DateTimeOffset>>("_sourceMissingSince")[lab.Source.Value] = lab.Clock.Now.AddHours(-1);
        await WriteProbe(lab, probe);
        var owner = lab.Owner.SessionId;
        await lab.Invoke("DiscoverAndProbeAsync", CancellationToken.None, true);
        Assert.Contains(lab.Sources[lab.Source.Value].State, new[] { SourceState.Unknown, SourceState.WaitingForKeyframe });
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.Contains(lab.Get<PortReservationManager>("_reservations").Snapshot(), lease => lease.Source == lab.Source);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    private sealed class ConnectedInventory : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                "{\"incomingStreams\":[{\"name\":\"source-A\",\"isConnected\":true}]}") });
    }

    private static async Task<SourceIdentity> PrepareInitialStart(OfflineFallbackTests.RecoveryLab lab)
    {
        await lab.InstallFakeMediaTools();
        await lab.Supervisor.StopAsync(lab.Source, CancellationToken.None);
        var standby = (SourceIdentity)typeof(BroadcastRouter.Web.Services.RouterCoordinator)
            .GetMethod("StandbyIdentity", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, ["TEST-OUTPUT"])!;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(lab.Root, "BroadcastRouter.FakeFfmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(lab.Root, "child.json"));
        await lab.Supervisor.StartOwnedProcessForTestingAsync(standby, start, purpose: RouteProcessPurpose.Fallback);
        lab.Events.Clear();
        lab.Sources[lab.Source.Value] = lab.Sources[lab.Source.Value] with { State = SourceState.Ready, Media = RecoveryReadinessTests.Av().Media };
        lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.Reserved };
        return standby;
    }

    private static Task StartInitial(OfflineFallbackTests.RecoveryLab lab) => lab.Invoke("StartRouteAsync", lab.Route,
        lab.Sources[lab.Source.Value], lab.Get<Dictionary<string, DeckLinkPort>>("_ports")["TEST-OUTPUT"],
        lab.Settings.Presets[0], CancellationToken.None, null);

    private static async Task AwaitPending(OfflineFallbackTests.RecoveryLab lab)
    {
        var gate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")[lab.Source.Value];
        var pending = (Task?)typeof(RecoveryReadinessGate).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate);
        if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndecodedRequiredTrackCannotStopFallback(bool packetOnlyAudio)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.SourceMediaProfiles.Add(new() { SourceId = lab.Source.Value,
            Expectation = packetOnlyAudio ? SourceMediaExpectation.AudioOnly : SourceMediaExpectation.ContinuousVideo });
        var json = packetOnlyAudio
            ? """{"streams":[{"codec_type":"video","codec_name":"h264","nb_read_frames":"50"},{"codec_type":"audio","codec_name":"aac","nb_read_packets":"50","nb_read_frames":"0"}]}"""
            : """{"streams":[{"codec_type":"audio","codec_name":"aac","nb_read_frames":"50"}]}""";
        await WriteProbe(lab, json);
        await Poll(lab);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
    }

    [Fact]
    public async Task ExplicitAudioOnlySelectsBlackEvenWhenProbeSeesVideo()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.SourceMediaProfiles.Add(new() { SourceId = lab.Source.Value, Expectation = SourceMediaExpectation.AudioOnly });
        await File.WriteAllTextAsync(Path.Combine(lab.Root, "live.json"), JsonSerializer.Serialize(new { captureArguments = true, events = Array.Empty<object>() }));
        await Poll(lab);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        var arguments = Path.Combine(lab.Root, "arguments.json");
        await FaultHarnessTests.Eventually(() => File.Exists(arguments));
        var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(arguments))!;
        Assert.Contains("1:v:0", args);
        Assert.DoesNotContain("0:v:0", args);
        Assert.NotNull(lab.Owner.SelectedMedia);
        Assert.False(lab.Owner.SelectedMedia.HasUsableVideo);
        Assert.Equal(SourceMediaExpectation.AudioOnly, lab.Owner.SelectedMedia.Expectation);
        var owner = lab.Owner.SessionId;
        var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
        lab.Settings.SourceMediaProfiles[0].Expectation = SourceMediaExpectation.ContinuousVideo;
        lab.Settings.SimulationMode = true; // Only the probe is simulated; the existing child is still real/owned.
        var observation = lab.Sources[lab.Source.Value] with { State = SourceState.PublisherActive, Media = null };
        var task = (Task)typeof(BroadcastRouter.Web.Services.RouterCoordinator)
            .GetMethod("ProbeObservationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lab.Coordinator, [observation, lab.Settings, CancellationToken.None, null])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var observed = (DiscoveredSource)result.GetType().GetProperty("Source")!.GetValue(result)!;
        Assert.True(observed.Media!.HasUsableVideo);
        await lab.Invoke("RecordMediaModeChangesAsync", CancellationToken.None);
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.False(lab.Owner.SelectedMedia.HasUsableVideo);
        Assert.Equal(stops, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
    }

    internal static Task WriteProbe(OfflineFallbackTests.RecoveryLab lab, string json) =>
        File.WriteAllTextAsync(Path.Combine(lab.Root, "probe.json"), JsonSerializer.Serialize(new {
            events = new[] { new { atMilliseconds = 0, channel = "stdout", text = json } }, exitAfterMilliseconds = 20 }));

    internal static async Task Poll(OfflineFallbackTests.RecoveryLab lab)
    {
        await lab.RestartDue();
        var gate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")[lab.Source.Value];
        var pending = (Task?)typeof(RecoveryReadinessGate).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate);
        if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await lab.RestartDue();
    }
}
