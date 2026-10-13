using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class IndependentMediaHealthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private static readonly MediaSignalKind[] Av = [MediaSignalKind.InputVideo, MediaSignalKind.InputAudio,
        MediaSignalKind.OutputVideo, MediaSignalKind.OutputAudio];

    [Fact]
    public void FreshHeartbeatCannotConcealTwoFailedMediaSignals()
    {
        var tracker = Warm(Av);
        tracker.ObserveHeartbeat(Now.AddSeconds(25));
        var result = tracker.Snapshot(Now.AddSeconds(25), true);
        Assert.Equal(MediaSignalState.Healthy, result.Heartbeat);
        Assert.True(result.ConfirmedMediaFailure);
    }

    [Fact]
    public void OnlyOneStalledSignalNeverAuthorizesDestruction()
    {
        var tracker = Warm(Av);
        for (var i = 41; i <= 60; i++)
            foreach (var kind in Av.Where(k => k != MediaSignalKind.InputAudio))
                tracker.Observe(kind, i, Now.AddMilliseconds(i * 500));
        var result = tracker.Snapshot(Now.AddSeconds(30), true);
        Assert.Equal(MediaSignalState.Failed, result.Signals.Single(s => s.Kind == MediaSignalKind.InputAudio).State);
        Assert.False(result.ConfirmedMediaFailure);
    }

    [Fact]
    public void RepeatedCounterIsNotSecondEvidence()
    {
        var tracker = Warm([MediaSignalKind.InputAudio]);
        for (var i = 0; i < 100; i++) tracker.Observe(MediaSignalKind.InputAudio, 40, Now.AddSeconds(30));
        Assert.False(tracker.Snapshot(Now.AddSeconds(30), true).ConfirmedMediaFailure);
    }

    [Fact]
    public void GeneratedVideoCannotCorroborateMissingRealAudio()
    {
        var tracker = Warm([MediaSignalKind.InputAudio, MediaSignalKind.OutputAudio]);
        for (var i = 41; i <= 60; i++)
        {
            tracker.Observe(MediaSignalKind.OutputVideo, i, Now.AddMilliseconds(i * 500));
            tracker.Observe(MediaSignalKind.OutputAudio, i, Now.AddMilliseconds(i * 500));
        }
        Assert.False(tracker.Snapshot(Now.AddSeconds(30), true).ConfirmedMediaFailure);
        Assert.DoesNotContain(tracker.Snapshot(Now.AddSeconds(30), true).Signals, s => s.Kind == MediaSignalKind.InputVideo);
    }

    [Fact]
    public void StillPictureAndDigitalSilenceWithAdvancingCountersAreHealthy()
    {
        var result = Warm(Av).Snapshot(Now.AddSeconds(20), true);
        Assert.All(result.Signals, signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
        Assert.False(result.ConfirmedMediaFailure);
    }

    [Fact]
    public void MissingInstrumentationStaysUnknownNotFailed()
    {
        var tracker = new MediaEvidenceTracker(Av, Now);
        tracker.ObserveHeartbeat(Now.AddHours(1));
        var result = tracker.Snapshot(Now.AddHours(1), true);
        Assert.All(result.Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
        Assert.False(result.ConfirmedMediaFailure);
    }

    [Fact]
    public void RecoveryRequiresTwoSecondsOfSustainedAdvancement()
    {
        var tracker = Warm(Av);
        Assert.True(tracker.Snapshot(Now.AddSeconds(25), true).ConfirmedMediaFailure);
        for (var i = 0; i <= 4; i++)
        {
            foreach (var kind in Av) tracker.Observe(kind, 100 + i, Now.AddSeconds(26).AddMilliseconds(i * 500));
            var expected = i < 4 ? MediaSignalState.Suspect : MediaSignalState.Healthy;
            Assert.All(tracker.Snapshot(Now.AddSeconds(26).AddMilliseconds(i * 500), true).Signals,
                signal => Assert.Equal(expected, signal.State));
        }
    }

    [Fact]
    public void InvalidatedTelemetryCannotBecomeCorroboratedLoss()
    {
        var tracker = Warm(Av);
        foreach (var kind in Av) tracker.Invalidate(kind, Now.AddSeconds(21));
        var result = tracker.Snapshot(Now.AddHours(1), true);
        Assert.All(result.Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
        Assert.False(result.ConfirmedMediaFailure);
    }

    [Fact]
    public void StartupQualificationDoesNotKillBeforeTwentySeconds()
    {
        var tracker = new MediaEvidenceTracker(Av, Now);
        for (var i = 0; i <= 6; i++)
            foreach (var kind in Av) tracker.Observe(kind, i, Now.AddMilliseconds(i * 500));
        Assert.False(tracker.Snapshot(Now.AddSeconds(19), true).ConfirmedMediaFailure);
        Assert.True(tracker.Snapshot(Now.AddSeconds(20), true).ConfirmedMediaFailure);
    }

    [Fact]
    public void ConfirmedProcessExitIsDefinitiveWithoutMediaCounters()
    {
        var result = new MediaEvidenceTracker(Av, Now).Snapshot(Now, false);
        Assert.Equal(MediaSignalState.Failed, result.Process);
        Assert.True(result.MayRecover);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ValidEarlyMediaThatStopsBeforeHealthyQualificationStillFailsAfterStartup(int advances)
    {
        var tracker = new MediaEvidenceTracker(Av, Now);
        for (var i = 0; i <= advances; i++)
            foreach (var kind in Av) tracker.Observe(kind, i, Now.AddMilliseconds(i * 500));
        Assert.False(tracker.Snapshot(Now.AddSeconds(19), true).MayRecover);
        var stopped = tracker.Snapshot(Now.AddSeconds(20), true);
        Assert.All(stopped.Signals, signal => Assert.Equal(MediaSignalState.Failed, signal.State));
        Assert.True(stopped.MayRecover);
    }

    [Fact]
    public void CounterResetRequiresFreshQualificationInsteadOfInventingStalledMedia()
    {
        var tracker = Warm(Av);
        foreach (var kind in Av) tracker.Observe(kind, 1, Now.AddSeconds(25));
        var reset = tracker.Snapshot(Now.AddSeconds(26), true);
        Assert.All(reset.Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
        Assert.False(reset.MayRecover);
        for (var i = 1; i <= 4; i++)
            foreach (var kind in Av) tracker.Observe(kind, i + 1, Now.AddSeconds(25).AddMilliseconds(i * 500));
        Assert.All(tracker.Snapshot(Now.AddSeconds(27), true).Signals,
            signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
    }

    private static MediaEvidenceTracker Warm(MediaSignalKind[] required)
    {
        var tracker = new MediaEvidenceTracker(required, Now);
        for (var i = 0; i <= 40; i++)
            foreach (var kind in required) tracker.Observe(kind, i, Now.AddMilliseconds(i * 500));
        return tracker;
    }
}
