using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class GrowthProbeEvidenceTests
{
    [Theory]
    [InlineData(false, 0.04)]
    [InlineData(true, 10)]
    public void ReportsLargestDecodedVideoGapSoTwoFramesCannotProveSustainedGrowth(bool sparse, double expected)
    {
        var video = sparse ? new[] { 0d, 10d } : Enumerable.Range(0, 251).Select(i => i / 25d).ToArray();
        var probe = FfprobeStreamProbe.Parse(MediaQualificationDurationTests.Json(video, []));
        Assert.NotNull(probe.MaximumVideoGapSeconds);
        Assert.Equal(expected, probe.MaximumVideoGapSeconds.Value, 6);
    }
}
