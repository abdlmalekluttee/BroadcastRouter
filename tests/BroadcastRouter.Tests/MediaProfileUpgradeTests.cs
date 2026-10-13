using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class MediaProfileUpgradeTests
{
    [Fact]
    public void SustainedRicherProfileClaimsExactlyOnceEvenAfterOwnerReplacement()
    {
        var policy = new MediaSessionProfilePolicy();
        var session = policy.Attach(Guid.NewGuid(), AutomaticMediaDetectionTests.Sample(false, true).Media!);
        Assert.True(policy.TryClaim(session, Evidence(), TimeSpan.FromSeconds(10)));
        Assert.False(policy.TryClaim(session, Evidence(), TimeSpan.FromSeconds(10)));
        var replacement = policy.Attach(Guid.NewGuid(), session.Media);
        Assert.False(policy.TryClaim(replacement, Evidence(), TimeSpan.FromSeconds(10)));
        policy.NewPublisherEpoch();
        var next = policy.Attach(Guid.NewGuid(), session.Media);
        Assert.True(policy.TryClaim(next, Evidence(), TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("short-media")]
    [InlineData("short-observation")]
    [InlineData("two-keyframes")]
    [InlineData("audio-gap")]
    [InlineData("old-owner")]
    [InlineData("old-epoch")]
    [InlineData("unknown-publisher")]
    [InlineData("forced-profile")]
    public void UnqualifiedGrowthNeverClaimsOrChangesTheSession(string defect)
    {
        var policy = new MediaSessionProfilePolicy();
        var media = AutomaticMediaDetectionTests.Sample(false, true).Media!;
        if (defect == "forced-profile") media = media with { SelectedOverride = SourceMediaOverride.ForceAudioOnly };
        var session = policy.Attach(Guid.NewGuid(), media);
        var probe = Evidence();
        var duration = TimeSpan.FromSeconds(10);
        if (defect == "short-media") probe = probe with { AdvancingVideoSeconds = 9.9 };
        if (defect == "two-keyframes") probe = probe with { MaximumVideoGapSeconds = 10 };
        if (defect == "audio-gap") probe = probe with { AdvancingAudioSeconds = 0 };
        if (defect == "short-observation") duration = TimeSpan.FromSeconds(1);
        if (defect == "old-owner") policy.Attach(Guid.NewGuid(), media);
        if (defect == "old-epoch") policy.NewPublisherEpoch();
        if (defect == "unknown-publisher") policy.InvalidateObservation();
        Assert.False(policy.TryClaim(session, probe, duration));
        Assert.False(session.Media.HasUsableVideo);
        Assert.Equal("aac", session.Media.AudioCodec);
    }

    [Fact]
    public void MediaLossDoesNotChangeTheLiveGraphButNewOwnerCanRedetectAudioOnly()
    {
        var policy = new MediaSessionProfilePolicy();
        var av = policy.Attach(Guid.NewGuid(), AutomaticMediaDetectionTests.Sample(true, true).Media!);
        Assert.False(policy.TryClaim(av, AutomaticMediaDetectionTests.Sample(false, true), TimeSpan.FromSeconds(10)));
        Assert.True(av.Media.HasUsableVideo);
        var audioOnly = policy.Attach(Guid.NewGuid(), AutomaticMediaDetectionTests.Sample(false, true).Media!);
        Assert.False(audioOnly.Media.HasUsableVideo);
        Assert.NotEqual(av.SessionId, audioOnly.SessionId);
    }

    internal static StreamProbeResult Evidence() => AutomaticMediaDetectionTests.Sample(true, true) with {
        AdvancingVideoSeconds = 10.1, AdvancingAudioSeconds = 10.1, MaximumVideoGapSeconds = 0.04 };
}
