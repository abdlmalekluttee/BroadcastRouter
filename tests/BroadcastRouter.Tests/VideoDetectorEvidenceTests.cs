using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class VideoDetectorEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private const string QueueWarning = "There are not enough buffered video frames. Video may misbehave!";

    // Unchanged R05 correctness assertion; promoted only in the fixing commit.
    [Fact, Trait("Finding", "R05")]
    public void DuplicateBurstRespectsConfirmationWindow()
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, new(100, 25, TimeSpan.FromSeconds(4), 0, 0, 1, Now, false),
            Now, TimeSpan.FromSeconds(8));
        var later = Now.AddMilliseconds(500);
        var restart = detector.Observe(101, new(112, 25, TimeSpan.FromSeconds(4.5), 0, 12, 1, later, false),
            later, TimeSpan.FromSeconds(8));
        Assert.False(restart, "R05: a 500ms duplicate burst must not bypass the confirmation window");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(25)]
    [InlineData(1000)]
    public void DuplicateMultiplicityCannotBypassElapsedSuspicion(long burst)
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, Progress(100, 0), Now, TimeSpan.FromSeconds(8));
        Assert.False(detector.Observe(101, Progress(100 + burst, burst), Now.AddMilliseconds(500), TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void SustainedDuplicationIsContextOnlyAfterTheWholeWindow()
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, Progress(100, 0), Now, TimeSpan.FromSeconds(8));
        Assert.False(detector.Observe(101, Progress(125, 25), Now.AddSeconds(1), TimeSpan.FromSeconds(8)));
        Assert.False(detector.Observe(101, Progress(300, 200), Now.AddMilliseconds(8999), TimeSpan.FromSeconds(8)));
        Assert.True(detector.Observe(101, Progress(325, 225), Now.AddSeconds(9), TimeSpan.FromSeconds(8)));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("counter")]
    [InlineData("healthy")]
    [InlineData("missing")]
    public void DiscontinuousObservationsDoNotInheritPreviousSuspicion(string reset)
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, Progress(100, 0), Now, TimeSpan.FromSeconds(8));
        Assert.False(detector.Observe(101, Progress(125, 25), Now.AddSeconds(1), TimeSpan.FromSeconds(8)));
        var pid = reset == "pid" ? 102 : 101;
        var frame = reset == "counter" ? 5 : 150;
        var duplicates = reset == "counter" ? 0 : 25;
        Assert.False(detector.Observe(pid, reset == "missing" ? null : Progress(frame, duplicates), Now.AddSeconds(2), TimeSpan.FromSeconds(8)));
        Assert.False(detector.Observe(pid, Progress(frame + 25, duplicates + 25), Now.AddSeconds(20), TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void RepeatedUnchangedProgressDoesNotCompleteTheWindow()
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, Progress(100, 0), Now, TimeSpan.FromSeconds(8));
        var same = Progress(125, 25);
        Assert.False(detector.Observe(101, same, Now.AddSeconds(1), TimeSpan.FromSeconds(8)));
        for (var second = 2; second <= 30; second++)
            Assert.False(detector.Observe(101, same, Now.AddSeconds(second), TimeSpan.FromSeconds(8)));
    }

    [Theory]
    [InlineData(MediaSignalState.Healthy, 6000, false)]
    [InlineData(MediaSignalState.Suspect, 6000, false)]
    [InlineData(MediaSignalState.Unknown, 6000, false)]
    [InlineData(MediaSignalState.Failed, -1, false)]
    [InlineData(MediaSignalState.Failed, 0, false)]
    [InlineData(MediaSignalState.Failed, 3999, false)]
    [InlineData(MediaSignalState.Failed, 4000, true)]
    [InlineData(MediaSignalState.Failed, 9000, true)]
    public void VideoWarningRequiresFailedDecodedVideoAndElapsedTime(MediaSignalState state, int ageMs, bool expected)
    {
        var media = new MediaEvidenceSnapshot(MediaSignalState.Healthy, MediaSignalState.Healthy,
            [new(MediaSignalKind.InputVideo, state, Now.AddMilliseconds(-ageMs))], false, false);
        var detector = new FfmpegMediaStarvationDetector();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(expected, detector.Observe(QueueWarning, Now, Now.AddMinutes(-1), TimeSpan.FromSeconds(5),
                out var category, out _, media));
            if (expected) Assert.Equal("DeckLinkVideoStarved", category);
        }
    }

    [Theory]
    [InlineData(MediaSignalKind.InputAudio, true)]
    [InlineData(MediaSignalKind.OutputVideo, true)]
    [InlineData(MediaSignalKind.InputVideo, false)]
    public void OtherMeasurementPointOrMissingVideoTimestampCannotProveLoss(MediaSignalKind kind, bool timestamp)
    {
        var media = new MediaEvidenceSnapshot(MediaSignalState.Healthy, MediaSignalState.Healthy,
            [new(kind, MediaSignalState.Failed, timestamp ? Now.AddMinutes(-1) : null)], false, false);
        var detector = new FfmpegMediaStarvationDetector();
        for (var i = 0; i < 3; i++)
            Assert.False(detector.Observe(QueueWarning, Now, Now.AddMinutes(-1), TimeSpan.FromSeconds(5), out _, out _, media));
    }

    [Theory]
    [InlineData("brmedia|pre|v|2|2|1/1|74", 2, 2)]
    [InlineData("brmedia|pre|v|74|74|1/25|74", 74, 74)]
    public void ExactBuildGeneratedRecordsPreserveDecoderIdentityInsteadOfFilteredCount(string record, long identity, long pts)
    {
        Assert.True(FfmpegMediaStats.TryParse(MediaSignalKind.InputVideo, record, out var observed));
        Assert.Equal(identity, observed.Counter);
        Assert.Equal(pts, observed.InputPts);
    }

    private static FfmpegProgressSnapshot Progress(long frame, long duplicates) =>
        new(frame, 25, TimeSpan.FromSeconds(frame / 25d), 0, duplicates, 1, Now, false);
}
