using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AutomaticMediaCoordinatorTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AutoHandoffWorksWithoutSavedMediaDeclaration(bool video, bool audio)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.SourceMediaProfiles.Clear();
        await System.IO.File.WriteAllTextAsync(Path.Combine(lab.Root, "live.json"), System.Text.Json.JsonSerializer.Serialize(new {
            captureArguments = true, events = new[] { new { atMilliseconds = 0, channel = "stdout", text = "frame=1\nprogress=continue" } } }));
        await DeclaredMediaCoordinatorTests.WriteProbe(lab, Json(video, audio));
        await Poll(lab);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        Assert.Equal(video, lab.Owner.SelectedMedia!.HasUsableVideo);
        Assert.Equal(audio, lab.Owner.SelectedMedia.AudioCodec is not null);
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        await FaultHarnessTests.Eventually(() => File.Exists(Path.Combine(lab.Root, "arguments.json")));
        var args = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(lab.Root, "arguments.json")))!;
        Assert.Equal(video, args.Contains("0:v:0"));
        Assert.Equal(audio, args.Contains("0:a:0"));
        Assert.Equal(!video, args.Any(a => a.StartsWith("color=", StringComparison.Ordinal)));
        Assert.Equal(!audio, args.Any(a => a.StartsWith("anullsrc=", StringComparison.Ordinal)));
        Assert.Single(lab.Get<PortReservationManager>("_reservations").Snapshot(), l => l.Source == lab.Source);
    }

    [Fact]
    public async Task CorroboratedVideoLossRecreatesSessionAndRedetectsAudioOnly()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        await Poll(lab);
        lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.Running };
        var original = lab.Owner.SessionId;
        var tracker = new MediaEvidenceTracker([MediaSignalKind.InputVideo, MediaSignalKind.OutputVideo,
            MediaSignalKind.InputAudio, MediaSignalKind.OutputAudio], lab.Clock.Now);
        for (var i = 0; i <= 6; i++)
        {
            foreach (var kind in Enum.GetValues<MediaSignalKind>()) tracker.Observe(kind, i, lab.Clock.Now);
            lab.Time.Advance(TimeSpan.FromMilliseconds(500));
        }
        for (var i = 7; i < 55; i++)
        {
            foreach (var kind in new[] { MediaSignalKind.InputAudio, MediaSignalKind.OutputAudio }) tracker.Observe(kind, i, lab.Clock.Now);
            tracker.ObserveHeartbeat(lab.Clock.Now);
            lab.Time.Advance(TimeSpan.FromMilliseconds(500));
        }
        var health = tracker.Snapshot(lab.Clock.Now, true);
        Assert.True(health.ConfirmedMediaFailure);
        Assert.All(health.Signals.Where(s => s.Kind is MediaSignalKind.InputAudio or MediaSignalKind.OutputAudio), s => Assert.Equal(MediaSignalState.Healthy, s.State));
        await lab.Invoke("RecoverConfirmedMediaAsync", lab.Supervisor, lab.Owner with { IndependentMedia = health }, lab.Route, CancellationToken.None);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
        Assert.Equal(4, lab.Route.RestartCount);
        await DeclaredMediaCoordinatorTests.WriteProbe(lab, Json(false, true));
        lab.Time.Advance(TimeSpan.FromMinutes(1));
        await Poll(lab);
        Assert.NotEqual(original, lab.Owner.SessionId);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        Assert.Equal(DetectedMediaProfile.AudioOnly, lab.Owner.SelectedMedia!.DetectedProfile);
        Assert.False(lab.Owner.SelectedMedia.HasUsableVideo);
    }

    [Theory]
    [InlineData(SourceMediaOverride.Auto)]
    [InlineData(SourceMediaOverride.ForceAudioVideo)]
    [InlineData(SourceMediaOverride.ForceVideoOnly)]
    [InlineData(SourceMediaOverride.ForceAudioOnly)]
    public async Task AudioOnlyOnMutedPresetKeepsStandbyWithExactAlarm(SourceMediaOverride mode)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.Presets[0].IncludeAudio = false;
        lab.Settings.SourceMediaProfiles[0].Override = mode;
        await DeclaredMediaCoordinatorTests.WriteProbe(lab, Json(false, true));
        var owner = lab.Owner.SessionId;
        await Poll(lab);
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        Assert.Equal("incompatible preset: audio-only source on a muted output", lab.Sources[lab.Source.Value].MediaQualificationDetail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioOnlyGainingSustainedVideoPerformsOnePlannedHandoff(bool invalidateAfterStop)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        await DeclaredMediaCoordinatorTests.WriteProbe(lab, Json(false, true));
        await Poll(lab);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        lab.Routes[lab.Source.Value] = lab.Route with { State = RouteState.Running };
        var original = lab.Owner.SessionId;
        var invalidated = false;
        lab.Supervisor.LifecycleChanged += e => {
            if (invalidateAfterStop && e.State == RouteProcessLifecycleState.StopRequested)
            {
                invalidated = true;
                lab.Settings.ConfigurationRevision++;
            }
        };
        var probe = MediaQualificationDurationTests.Json(Enumerable.Range(0, 261).Select(i => i / 25d).ToArray(),
            Enumerable.Range(0, 490).Select(i => (i * 1024 / 48000d, 1024)).ToArray());
        await System.IO.File.WriteAllTextAsync(Path.Combine(lab.Root, "probe.json"), System.Text.Json.JsonSerializer.Serialize(new {
            events = new[] { new { atMilliseconds = 0, channel = "stdout", text = probe } }, exitAfterMilliseconds = 2200 }));
        var stops = lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested);
        for (var i = 0; i < 350 && lab.Owner.SessionId == original; i++)
        {
            await lab.Invoke("RecordMediaModeChangesAsync", CancellationToken.None);
            await Task.Delay(10);
            lab.Time.Advance(TimeSpan.FromMilliseconds(50));
        }
        if (invalidateAfterStop)
        {
            Assert.True(invalidated);
            Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback), "An invalidated planned handoff must restore standby after stopping its old live owner.");
            return;
        }
        Assert.NotEqual(original, lab.Owner.SessionId);
        Assert.True(lab.Owner.SelectedMedia!.HasUsableVideo);
        Assert.Equal(stops + 1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.StopRequested));
        Assert.Equal(3, lab.Route.RestartCount); // planned, not a fault/retry increment
    }

    [Fact]
    public async Task MutedAvKeepsAudioDisabledInSelectedGraph()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.Presets[0].IncludeAudio = false;
        await Poll(lab);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Live));
        Assert.Equal(DetectedMediaProfile.AudioVideo, lab.Owner.SelectedMedia!.DetectedProfile);
        Assert.True(lab.Owner.SelectedMedia.AudioDisabledByPreset);
    }

    [Fact]
    public async Task MutedAvStillWaitsForInputAudioQualification()
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.Presets[0].IncludeAudio = false;
        await DeclaredMediaCoordinatorTests.WriteProbe(lab, MediaQualificationDurationTests.Json(MediaQualificationDurationTests.Video(), [(0, 1024)]));
        var owner = lab.Owner.SessionId;
        await Poll(lab);
        Assert.Equal(owner, lab.Owner.SessionId);
        Assert.DoesNotContain(lab.Events, e => e.State == RouteProcessLifecycleState.StopRequested);
        Assert.Contains("audio", lab.Sources[lab.Source.Value].MediaQualificationDetail!, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Json(bool video, bool audio)
    {
        var document = JsonNode.Parse(MediaQualificationDurationTests.Json(video ? MediaQualificationDurationTests.Video() : [],
            audio ? MediaQualificationDurationTests.Audio() : []))!;
        var streams = document["streams"]!.AsArray();
        if (!audio) streams.RemoveAt(1);
        if (!video) streams.RemoveAt(0);
        return document.ToJsonString();
    }

    internal static async Task Drive(OfflineFallbackTests.RecoveryLab lab, Task task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++)
        {
            // These helpers are real finite child processes on a fake clock.
            // Pause virtual time while they start/finish so CI scheduling load
            // cannot fabricate a detection deadline. Hung-helper/deadline cases
            // are tested separately with controlled TaskCompletionSources.
            var detectors = lab.Get<ConcurrentDictionary<string, AutomaticMediaDetection>>("_mediaDetectors");
            foreach (var detector in detectors.Values)
                if (typeof(AutomaticMediaDetection).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(detector) is Task pending && !pending.IsCompleted)
                    await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(2);
            if (!task.IsCompleted) lab.Time.Advance(TimeSpan.FromMilliseconds(50));
        }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal static async Task Poll(OfflineFallbackTests.RecoveryLab lab)
    {
        await lab.RestartDue();
        var gate = lab.Get<ConcurrentDictionary<string, RecoveryReadinessGate>>("_recoveryReadiness")[lab.Source.Value];
        var pending = (Task?)typeof(RecoveryReadinessGate).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate);
        if (pending is not null) await Drive(lab, pending);
        await lab.RestartDue();
    }
}
