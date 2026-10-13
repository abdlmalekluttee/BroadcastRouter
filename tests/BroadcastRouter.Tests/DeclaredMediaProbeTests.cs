using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DeclaredMediaProbeTests
{
    [Fact]
    public void OneDecodedImageMustNotBeDiscardedAsGeneratedBlack()
    {
        var probe = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"video","codec_name":"h264","avg_frame_rate":"25/1","nb_read_frames":"1"},{"codec_type":"audio","codec_name":"aac","nb_read_frames":"48"}]}""");
        Assert.True(probe.FramesReceived, "A decoded image was discarded by a cadence guess.");
        Assert.True(probe.Media!.HasUsableVideo);
    }

    [Fact]
    public void AdvertisedVideoWithoutDecodedFrameWaitsForKeyframe()
    {
        var probe = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"video","codec_name":"h264","nb_read_frames":"0"},{"codec_type":"audio","codec_name":"aac","nb_read_frames":"48"}]}""");
        Assert.Equal("WaitingForKeyframe", SourceProbeReadinessPolicy.Resolve(probe).ToString());
    }

    [Fact]
    public void MissingRequiredVideoIsUnknownNotInferredAudioOnly()
    {
        var probe = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"audio","codec_name":"aac","nb_read_frames":"48"}]}""");
        Assert.Equal("Unknown", SourceProbeReadinessPolicy.Resolve(probe).ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"streams\":[]}")]
    [InlineData("{not-json")]
    [InlineData("[]")]
    [InlineData("{\"streams\":[null]}")]
    public void IncompleteProbeIsUnknown(string json)
    {
        Assert.Equal("Unknown", SourceProbeReadinessPolicy.Resolve(FfprobeStreamProbe.Parse(json)).ToString());
    }
}
