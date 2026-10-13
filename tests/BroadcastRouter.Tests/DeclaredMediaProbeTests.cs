using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DeclaredMediaProbeTests
{
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
    public void IncompleteProbeIsUnknown(string json)
    {
        Assert.Equal("Unknown", SourceProbeReadinessPolicy.Resolve(FfprobeStreamProbe.Parse(json)).ToString());
    }
}
