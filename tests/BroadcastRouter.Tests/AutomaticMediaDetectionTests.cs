using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AutomaticMediaDetectionTests
{
    [Fact]
    public async Task FailedOpenRetainsExactFailureAndDoesNotMultiplyOfflineProbes()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var detector = new AutomaticMediaDetection(new Probe(() => {
            calls++;
            return new(false, false, null, "RtspNotFound", "Synthetic RTSP 404");
        }), new(time));
        var task = detector.DetectAsync(new("rtsp://example.invalid/test"), SourceMediaOverride.Auto, CancellationToken.None);
        await Advance(time, 5);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("RtspNotFound", result.Probe.FailureCategory);
        Assert.False(result.Qualified);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true, false, DetectedMediaProfile.VideoOnly)]
    [InlineData(false, true, DetectedMediaProfile.AudioOnly)]
    [InlineData(true, true, DetectedMediaProfile.AudioVideo)]
    public async Task FirstTrackNeverShortensFiveSecondObservation(bool video, bool audio, DetectedMediaProfile expected)
    {
        var time = new ManualTimeProvider();
        var detector = new AutomaticMediaDetection(new Probe(() => Sample(video, audio)), new(time));
        var task = detector.DetectAsync(new("rtsp://example.invalid/test"), SourceMediaOverride.Auto, CancellationToken.None);
        await Advance(time, 4);
        Assert.False(task.IsCompleted, "Detection returned before the second track observation window elapsed.");
        await Advance(time, 1);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Qualified, result.Probe.Detail);
        Assert.Equal(expected, result.Profile);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrackArrivingAtThreeSecondsIsIncludedAndQualified(bool videoFirst)
    {
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        var start = clock.Now;
        var detector = new AutomaticMediaDetection(new Probe(() => {
            var late = clock.Now - start >= TimeSpan.FromSeconds(3);
            return Sample(videoFirst || late, !videoFirst || late);
        }), clock);
        var task = detector.DetectAsync(new("rtsp://example.invalid/test"), SourceMediaOverride.Auto, CancellationToken.None);
        await Advance(time, 5);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Qualified, result.Probe.Detail);
        Assert.Equal(DetectedMediaProfile.AudioVideo, result.Profile);
    }

    [Fact]
    public async Task OneDecodedKeyframeCannotDisappearFromTheAdmissionContract()
    {
        var time = new ManualTimeProvider();
        var call = 0;
        var detector = new AutomaticMediaDetection(new Probe(() => ++call == 1
            ? Sample(true, true) with { AdvancingVideoSeconds = 0 }
            : Sample(false, true)), new(time));
        var task = detector.DetectAsync(new("rtsp://example.invalid/test"), SourceMediaOverride.Auto, CancellationToken.None);
        await Advance(time, 5);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Qualified);
        Assert.Equal(DetectedMediaProfile.AudioVideo, result.Profile);
        Assert.Contains("video", result.Probe.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HungProbeTimesOutWithoutOpeningAnOverlappingHelper()
    {
        var time = new ManualTimeProvider();
        var blocked = new TaskCompletionSource<StreamProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BlockingProbe(blocked.Task);
        var detector = new AutomaticMediaDetection(probe, new(time));
        var uri = new Uri("rtsp://example.invalid/test");
        var task = detector.DetectAsync(uri, SourceMediaOverride.Auto, CancellationToken.None);
        await Advance(time, 5);
        Assert.False((await task.WaitAsync(TimeSpan.FromSeconds(2))).Qualified);
        var again = await detector.DetectAsync(uri, SourceMediaOverride.Auto, CancellationToken.None);
        Assert.False(again.Qualified);
        Assert.Equal(1, probe.Calls);
        blocked.SetResult(Sample(true, true));
    }

    internal static StreamProbeResult Sample(bool video, bool audio) => RecoveryReadinessTests.Av() with {
        FramesReceived = video, DecodedAudioReceived = audio, AudioReceived = audio,
        AdvancingVideoSeconds = video ? 1.1 : 0, AdvancingAudioSeconds = audio ? 1.1 : 0,
        Media = RecoveryReadinessTests.Av().Media! with { HasUsableVideo = video,
            VideoCodec = video ? "h264" : null, AudioCodec = audio ? "aac" : null } };

    internal static async Task Advance(ManualTimeProvider time, double seconds)
    {
        for (var i = 0; i < (int)(seconds * 20); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(2); // let continuations register their next fake timer
        }
    }

    private sealed class Probe(Func<StreamProbeResult> sample) : IStreamProbe
    {
        public Task<StreamProbeResult> ProbeAsync(Uri uri, CancellationToken token) => Task.FromResult(sample());
    }
    private sealed class BlockingProbe(Task<StreamProbeResult> result) : IStreamProbe
    {
        public int Calls;
        public Task<StreamProbeResult> ProbeAsync(Uri uri, CancellationToken token) { Calls++; return result; }
    }
}
