using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegMediaStatsTests
{
    // Break caught: filtered output counters must not masquerade as decoded input.
    [Theory]
    [InlineData(MediaSignalKind.InputVideo, "brmedia|pre|v|24|24|1/25|48", 24, 24, 1, 25)]
    [InlineData(MediaSignalKind.InputAudio, "brmedia|pre|a|46|47104|1/48000|98", 46, 47104, 1, 48000)]
    [InlineData(MediaSignalKind.InputAudio, "brmedia|pre|a|0|-1024|1/48000|0", 0, -1024, 1, 48000)]
    public void InputUsesDecoderFrameAndPtsNotEncoderCounter(MediaSignalKind channel, string line,
        long counter, long pts, long numerator, long denominator)
    {
        Assert.True(FfmpegMediaStats.TryParse(channel, line, out var observation));
        Assert.Equal(channel, observation.Kind);
        Assert.Equal(counter, observation.Counter);
        Assert.Equal(pts, observation.InputPts);
        Assert.Equal(numerator, observation.TimeBaseNumerator);
        Assert.Equal(denominator, observation.TimeBaseDenominator);
    }

    [Theory]
    [InlineData(MediaSignalKind.OutputVideo, "brmedia|mux|v|25", 25)]
    [InlineData(MediaSignalKind.OutputAudio, "brmedia|mux|a|47", 47)]
    public void MuxCounterIsSubmissionEvidenceNotInput(MediaSignalKind channel, string line, long counter)
    {
        Assert.True(FfmpegMediaStats.TryParse(channel, line, out var observation));
        Assert.Equal(channel, observation.Kind);
        Assert.Equal(counter, observation.Counter);
        Assert.Null(observation.InputPts);
    }

    [Theory]
    [InlineData("brmedia|pre|v|-1|24|1/25|48")]
    [InlineData("brmedia|pre|v|24|9223372036854775807|1/25|48")]
    [InlineData("brmedia|pre|v|24|-9223372036854775808|1/25|48")]
    [InlineData("brmedia|pre|v|24|24|0/1|48")]
    [InlineData("brmedia|pre|v|24|24|1/0|48")]
    [InlineData("brmedia|pre|v|24|24|1/25|-1")]
    [InlineData("brmedia|pre|v|24|24|1/25|48|extra")]
    [InlineData("brmedia|pre|v|24|24|1/25|48brmedia|mux|v|49")]
    [InlineData("brmedia|pre|a|24|24|1/25|48")]
    [InlineData("brmedia|mux|v|48")]
    [InlineData("frame=48")]
    [InlineData("")]
    public void UnknownMetadataCorruptionAndWrongChannelAreNotMediaLoss(string line)
    {
        Assert.False(FfmpegMediaStats.TryParse(MediaSignalKind.InputVideo, line, out _));
    }

    [Fact]
    public void UnboundedAndOutOfRangeRecordsAreRejected()
    {
        Assert.False(FfmpegMediaStats.TryParse(MediaSignalKind.InputVideo, new string('9', 65536), out _));
        Assert.False(FfmpegMediaStats.TryParse(MediaSignalKind.OutputAudio, "brmedia|mux|a|9223372036854775808", out _));
        Assert.False(FfmpegMediaStats.TryParse((MediaSignalKind)99, "brmedia|mux|a|1", out _));
    }
}
