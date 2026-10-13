using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AutomaticMediaPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1.1, true)]
    public void AmbiguousSparseInputKeepsVideoIdentityAndUnknownHealth(double span, bool qualified)
    {
        var raw = AutomaticMediaDetectionTests.Sample(true, false) with {
            AdvancingVideoSeconds = span, MaximumVideoGapSeconds = span == 0 ? null : span };
        var result = SourceMediaPolicy.Apply(raw, new());
        Assert.Equal(SourceMediaExpectation.SparseVideo, result.Media!.Expectation);
        Assert.True(result.Media.HasUsableVideo);
        Assert.Equal(DetectedMediaProfile.VideoOnly, result.Media.DetectedProfile);
        Assert.Equal(qualified, result.FailureCategory is null);
    }

    // Catches carrying a static declaration (including an old audio-only choice)
    // forward as an admission requirement after upgrading to automatic detection.
    [Theory]
    [InlineData("{}", true, true)]
    [InlineData("{}", true, false)]
    [InlineData("{}", false, true)]
    [InlineData("{\"SourceMediaProfiles\":[{\"SourceId\":\"TEST/live/default/synthetic\",\"Expectation\":2,\"RequireAudio\":true}]}", true, false)]
    public void UpgradeDefaultsToObservedTracksWithoutDeclaration(string settingsJson, bool video, bool audio)
    {
        var settings = JsonSerializer.Deserialize<OperatorSettings>(settingsJson)!;
        var profile = SourceMediaPolicy.Get(settings, "TEST/live/default/synthetic");
        var raw = RecoveryReadinessTests.Av() with {
            FramesReceived = video, DecodedAudioReceived = audio, AudioReceived = audio,
            AdvancingVideoSeconds = video ? 1.2 : 0, AdvancingAudioSeconds = audio ? 1.2 : 0,
            Media = RecoveryReadinessTests.Av().Media! with {
                VideoCodec = video ? "h264" : null, HasUsableVideo = video,
                AudioCodec = audio ? "aac" : null } };
        var result = SourceMediaPolicy.Apply(raw, profile);
        Assert.Null(result.FailureCategory);
        Assert.Equal(video, result.Media!.HasUsableVideo);
        Assert.Equal(audio, result.Media.AudioCodec is not null);
    }

    [Fact]
    public void MutedOutputStillRequiresPresentInputAudioToQualify()
    {
        var raw = RecoveryReadinessTests.Av() with { AdvancingAudioSeconds = 0.3 };
        var result = SourceMediaPolicy.Apply(raw, new(), includeOptionalAudio: false);
        Assert.Equal("Unknown", result.FailureCategory);
        Assert.Contains("audio", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }
}
