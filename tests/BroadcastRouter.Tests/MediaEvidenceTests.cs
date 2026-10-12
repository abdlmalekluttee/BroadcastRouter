using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class MediaEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");

    [Fact]
    public void HeartbeatWithoutMediaAdvancementRemainsStalled()
    {
        var parser = new FfmpegProgressParser();
        Read(parser, 100, 4_000_000, Now);
        var snapshot = Read(parser, 100, 4_000_000, Now.AddSeconds(30));
        Assert.True(FfmpegStallDetector.IsStalled(true, snapshot, Now.AddSeconds(30), TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(101, 4_000_000)]
    [InlineData(100, 4_040_000)]
    public void ActualCounterAdvancementRefreshesOutputEvidence(long frame, long microseconds)
    {
        var parser = new FfmpegProgressParser();
        Read(parser, 100, 4_000_000, Now);
        var snapshot = Read(parser, frame, microseconds, Now.AddSeconds(30));
        Assert.False(FfmpegStallDetector.IsStalled(true, snapshot, Now.AddSeconds(30), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CounterRegressionDoesNotRefreshOutputEvidence()
    {
        var parser = new FfmpegProgressParser();
        Read(parser, 100, 4_000_000, Now);
        var snapshot = Read(parser, 99, 3_000_000, Now.AddSeconds(30));
        Assert.True(FfmpegStallDetector.IsStalled(true, snapshot, Now.AddSeconds(30), TimeSpan.FromSeconds(10)));
    }

    private static FfmpegProgressSnapshot Read(FfmpegProgressParser parser, long frame, long microseconds, DateTimeOffset at)
    {
        parser.Accept($"frame={frame}", at);
        parser.Accept($"out_time_us={microseconds}", at);
        return parser.Accept("progress=continue", at)!;
    }
}
