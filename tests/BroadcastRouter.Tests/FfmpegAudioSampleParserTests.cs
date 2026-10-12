using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegAudioSampleParserTests
{
    [Theory]
    [InlineData("frame:46   pts:47104   pts_time:0.981333", "48000.000000", 46, 47104, "0.981333", 48000)]
    [InlineData("frame:0    pts:-1024   pts_time:-0.0213333", "1024.000000", 0, -1024, "-0.0213333", 1024)]
    public void MeasuresPcmSamplesInsteadOfResamplerOrEncoderFrames(string header, string count,
        long frame, long pts, string seconds, long samples)
    {
        var parser = new FfmpegAudioSampleParser();
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept(header, out _));
        Assert.Equal(AudioSampleRecordState.Sample, parser.Accept("lavfi.astats.Overall.Number_of_samples=" + count, out var result));
        Assert.Equal(new(samples, frame, pts, decimal.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), result);
    }

    [Fact]
    public void EachCountRequiresItsOwnHeaderAndCannotReuseOldProvenance()
    {
        var parser = new FfmpegAudioSampleParser();
        const string count = "lavfi.astats.Overall.Number_of_samples=1024.000000";
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept(count, out _));
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept("frame:0 pts:0 pts_time:0", out _));
        Assert.Equal(AudioSampleRecordState.Sample, parser.Accept(count, out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept(count, out _));
    }

    [Theory]
    [InlineData("frame:0 pts:NOPTS pts_time:NOPTS")]
    [InlineData("frame:-1 pts:0 pts_time:0")]
    [InlineData("frame:0 pts:0 pts_time:NaN")]
    [InlineData("frame:0 pts:0 pts_time:0 extra")]
    [InlineData("frame:0 pts:9223372036854775808 pts_time:0")]
    [InlineData("frame:0 pts:-9223372036854775808 pts_time:0")]
    public void MissingOrCorruptTimestampIsUnknownNotInventedAdvancement(string header)
    {
        var parser = new FfmpegAudioSampleParser();
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept(header, out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept("lavfi.astats.Overall.Number_of_samples=1024.000000", out _));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1024.5")]
    [InlineData("NaN")]
    [InlineData("9223372036854775808.000000")]
    [InlineData("1024.000000extra")]
    public void InvalidSampleCountClearsPendingHeader(string count)
    {
        var parser = new FfmpegAudioSampleParser();
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept("frame:0 pts:0 pts_time:0", out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept("lavfi.astats.Overall.Number_of_samples=" + count, out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept("lavfi.astats.Overall.Number_of_samples=2048.000000", out _));
    }

    [Fact]
    public void MissingCountAndOversizedRecordInvalidateButNextCompletePairRecovers()
    {
        var parser = new FfmpegAudioSampleParser();
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept("frame:0 pts:0 pts_time:0", out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept("frame:1 pts:1024 pts_time:0.0213333", out _));
        Assert.Equal(AudioSampleRecordState.Sample, parser.Accept("lavfi.astats.Overall.Number_of_samples=2048.000000", out var second));
        Assert.Equal(2048, second.Samples);
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept("frame:2 pts:2048 pts_time:0.0426667", out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept(new string('9', 65536), out _));
        Assert.Equal(AudioSampleRecordState.Invalid, parser.Accept("lavfi.astats.Overall.Number_of_samples=3072.000000", out _));
        Assert.Equal(AudioSampleRecordState.Header, parser.Accept("frame:3 pts:3072 pts_time:0.064", out _));
        Assert.Equal(AudioSampleRecordState.Sample, parser.Accept("lavfi.astats.Overall.Number_of_samples=4096.000000", out var recovered));
        Assert.Equal(4096, recovered.Samples);
    }
}
