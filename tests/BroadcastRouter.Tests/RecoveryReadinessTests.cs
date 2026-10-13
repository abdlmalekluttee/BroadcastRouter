using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class RecoveryReadinessTests
{
    // Reflection allows a behavioral RED test before the policy type exists.
    private static dynamic NewGate(SupervisionClock clock)
    {
        var type = typeof(SupervisionClock).Assembly.GetType("BroadcastRouter.Application.RecoveryReadinessGate");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, clock)!;
    }

    internal static StreamProbeResult Av() => MediaQualificationDurationTests.Probe(
        MediaQualificationDurationTests.Video(), MediaQualificationDurationTests.Audio());

    [Fact]
    public void OfflineDayUsesBoundedProbesAndNeverProducesHandoff()
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        dynamic gate = NewGate(clock);
        var count = 0;
        Func<Task<StreamProbeResult>> probe = () => { count++; return Task.FromResult(new StreamProbeResult(false, false, null, "RtspNotFound", "synthetic")); };
        for (var second = 0; second < 86400; second++)
        {
            Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(2884, count);
    }

    [Fact]
    public async Task PendingProbeDoesNotBlockAndOneResultIsConsumedOnce()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        var complete = new TaskCompletionSource<StreamProbeResult>();
        var count = 0;
        Func<Task<StreamProbeResult>> probe = () => { count++; return complete.Task; };
        for (var i = 0; i < 50; i++) Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        Assert.Equal(1, count);
        complete.SetResult(Av());
        await FaultHarnessTests.Eventually(() => (object?)gate.Poll("synthetic", true, true, probe) is not null);
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        Assert.Equal(1, count);
    }

    [Fact]
    public void DisconnectDuringQualificationRejectsCompletedOldSession()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        var complete = new TaskCompletionSource<StreamProbeResult>();
        Func<Task<StreamProbeResult>> probe = () => complete.Task;
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        gate.Invalidate();
        complete.SetResult(Av());
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
    }

    [Fact]
    public void PacketCountsWithoutDecodedAudioDoNotQualifyAv()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        var packetsOnly = FfprobeStreamProbe.Parse("""
          {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1","nb_read_frames":"50"},
          {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_packets":"94","nb_read_frames":"0"}]}
          """);
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(packetsOnly);
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void QualificationRequiresOnlyTracksExpectedByTheRoute(bool video, bool audio)
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        var result = Av() with { FramesReceived = video };
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(result);
        Assert.NotNull((object?)gate.Poll("synthetic", video, audio, probe));
    }

    [Fact]
    public void UnknownPreviousTracksCanQualifyAnAudioOnlySource()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        var audio = FfprobeStreamProbe.Parse("""
          {"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_frames":"94"}]}
          """);
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(audio);
        Assert.NotNull((object?)gate.Poll("synthetic", (bool?)null, (bool?)null, probe));
    }

    [Fact]
    public async Task SimulationCanQualifyDecodedAudio()
    {
        var result = await new SimulationStreamProbe().ProbeAsync(new Uri("rtsp://example.invalid/fixture"), CancellationToken.None);
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(result);
        Assert.NotNull((object?)gate.Poll("simulation", true, true, probe));
    }

    [Fact]
    public void ProofExpiresBeforeHandoffAndCannotBeReused()
    {
        var time = new ManualTimeProvider();
        dynamic gate = NewGate(new SupervisionClock(time));
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(Av());
        var proof = (StreamProbeResult)gate.Poll("synthetic", true, true, probe);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.False((bool)gate.TryAuthorizeHandoff(proof));
        Assert.False((bool)gate.TryAuthorizeHandoff(proof));
    }

    [Fact]
    public void EpochAccelerationDoesNotForgiveProbeDebt()
    {
        var time = new ManualTimeProvider();
        dynamic gate = NewGate(new SupervisionClock(time));
        var calls = 0;
        Func<Task<StreamProbeResult>> probe = () => { calls++; return Task.FromResult(new StreamProbeResult(false, false, null, "RtspNotFound", "synthetic")); };
        for (var i = 0; i < 6; i++) { gate.Poll("synthetic", true, true, probe); time.Advance(TimeSpan.FromSeconds(30)); }
        gate.Poll("synthetic", true, true, probe);
        var before = calls;
        gate.AccelerateForPublisherEpoch();
        gate.Poll("synthetic", true, true, probe);
        Assert.Equal(before + 1, calls);
        time.Advance(TimeSpan.FromSeconds(29));
        gate.Poll("synthetic", true, true, probe);
        Assert.Equal(before + 1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        gate.Poll("synthetic", true, true, probe);
        Assert.Equal(before + 2, calls);
    }

    [Fact]
    public async Task ReconnectWaitsForOldProbeThenRunsOneFreshProbe()
    {
        var time = new ManualTimeProvider();
        dynamic gate = NewGate(new SupervisionClock(time));
        var pending = new TaskCompletionSource<StreamProbeResult>();
        var calls = 0;
        Func<Task<StreamProbeResult>> probe = () => ++calls == 1 ? pending.Task : Task.FromResult(Av());
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        gate.AccelerateForPublisherEpoch();
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        Assert.Equal(1, calls);
        pending.SetResult(Av());
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        await FaultHarnessTests.Eventually(() => (object?)gate.Poll("synthetic", true, true, probe) is not null);
        Assert.Equal(2, calls);
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FirstPublisherObservationAfterProofInvalidatesUnknownGeneration()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(Av());
        var proof = (StreamProbeResult)gate.Poll("synthetic", true, true, probe);
        gate.ObservePublisher(false);
        Assert.False((bool)gate.TryAuthorizeHandoff(proof));
    }

    [Fact]
    public void ConfigurationChangeInvalidatesCompletedProof()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        Func<Task<StreamProbeResult>> probe = () => Task.FromResult(Av());
        var proof = (StreamProbeResult)gate.Poll("before", true, true, probe);
        gate.Poll("after", true, true, probe);
        Assert.False((bool)gate.TryAuthorizeHandoff(proof));
    }

    [Fact]
    public void ExceptionIsFailedProofNotPermissionToStopFallback()
    {
        dynamic gate = NewGate(new SupervisionClock(new ManualTimeProvider()));
        Func<Task<StreamProbeResult>> probe = () => throw new IOException("synthetic");
        Assert.Null((object?)gate.Poll("synthetic", true, true, probe));
    }
}
