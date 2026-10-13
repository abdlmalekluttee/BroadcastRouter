using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SourceMediaPolicyTests
{
    [Theory]
    [InlineData(SourceMediaExpectation.ContinuousVideo, 0, true, "WaitingForKeyframe", false)]
    [InlineData(SourceMediaExpectation.SparseVideo, 0, true, "WaitingForKeyframe", false)]
    [InlineData(SourceMediaExpectation.SparseVideo, 1, true, null, true)]
    [InlineData(SourceMediaExpectation.ContinuousVideo, 1, true, "Unknown", true)]
    [InlineData(SourceMediaExpectation.AudioOnly, 1, true, null, false)]
    [InlineData(SourceMediaExpectation.AudioOnly, 0, true, null, false)]
    [InlineData(SourceMediaExpectation.AudioOnly, 1, false, "Unknown", false)]
    [InlineData(SourceMediaExpectation.ContinuousVideo, 1, false, "Unknown", true)]
    [InlineData(SourceMediaExpectation.SparseVideo, 1, false, "Unknown", true)]
    public void RequiredTracksAreQualifiedFromDecodedFacts(SourceMediaExpectation expectation, int frames,
        bool pcm, string? failure, bool selectedVideo)
    {
        var probe = MediaQualificationDurationTests.Probe(frames == 0 ? [] : [0], pcm ? MediaQualificationDurationTests.Audio() : [])
            with { FramesReceived = frames > 0, Media = RecoveryReadinessTests.Av().Media! with { HasUsableVideo = frames > 0 }, DecodedAudioReceived = pcm };
        var qualified = SourceMediaPolicy.Apply(probe, new() { Expectation = expectation });
        Assert.Equal(failure, qualified.FailureCategory);
        Assert.Equal(selectedVideo, qualified.Media!.HasUsableVideo);
        Assert.Equal(expectation, qualified.Media.Expectation);
        Assert.Equal(failure is null ? SourceState.Ready : failure == "WaitingForKeyframe"
            ? SourceState.WaitingForKeyframe : SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(qualified));
    }

    [Theory]
    [InlineData(SourceMediaExpectation.ContinuousVideo)]
    [InlineData(SourceMediaExpectation.SparseVideo)]
    public void AudioCannotSatisfyMissingRequiredVideo(SourceMediaExpectation expectation)
    {
        var raw = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"audio","codec_name":"aac","nb_read_frames":"20"}]}""");
        var probe = SourceMediaPolicy.Apply(raw, new() { Expectation = expectation });
        Assert.Equal("Unknown", probe.FailureCategory);
        Assert.False(probe.Media!.HasUsableVideo);
    }

    [Fact]
    public void RequiredAudioWaitsForDecodedPcmNotPackets()
    {
        var raw = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"video","codec_name":"h264","nb_read_frames":"50"},{"codec_type":"audio","codec_name":"aac","nb_read_packets":"50","nb_read_frames":"0"}]}""");
        var probe = SourceMediaPolicy.Apply(raw, new() { RequireAudio = true });
        Assert.Equal("Unknown", probe.FailureCategory);
        Assert.Equal(SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(probe));
    }

    [Fact]
    public async Task ExactSourceDeclarationRoundTripsWithoutNameSpecialCases()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-recovery-declared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new OperatorSettings { SourceMediaProfiles = [new() {
                SourceId = "TEST/live/_definst_/synthetic", Expectation = SourceMediaExpectation.SparseVideo, RequireAudio = true }] };
            var store = new SqliteDataStore(Path.Combine(root, "test.db"));
            await store.InitializeAsync();
            await store.SaveSettingsAsync(settings);
            var loaded = await store.LoadSettingsAsync();
            var profile = SourceMediaPolicy.Get(loaded, "TEST/live/_definst_/synthetic");
            Assert.Equal(SourceMediaExpectation.SparseVideo, profile.Expectation);
            Assert.True(profile.RequireAudio);
            Assert.Equal(SourceMediaExpectation.Undeclared, SourceMediaPolicy.Get(loaded, "TEST/live/_definst_/other").Expectation);
            profile.Expectation = SourceMediaExpectation.AudioOnly;
            Assert.Equal(SourceMediaExpectation.SparseVideo, SourceMediaPolicy.Get(loaded, "TEST/live/_definst_/synthetic").Expectation);
            loaded.SourceMediaProfiles.Add(new() { SourceId = "TEST/live/_definst_/synthetic" });
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSettingsAsync(loaded));
            Assert.Single((await store.LoadSettingsAsync()).SourceMediaProfiles);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); await FixtureDirectory.DeleteAsync(root); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("invalid-mode")]
    [InlineData("blank-id")]
    public void InvalidDeclarationRejected(string defect)
    {
        var settings = new OperatorSettings { SourceMediaProfiles = [new() { SourceId = "TEST/live/_definst_/synthetic" }] };
        if (defect == "duplicate") settings.SourceMediaProfiles.Add(new() { SourceId = "TEST/live/_definst_/synthetic" });
        if (defect == "invalid-mode") settings.SourceMediaProfiles[0].Expectation = (SourceMediaExpectation)99;
        if (defect == "blank-id") settings.SourceMediaProfiles[0].SourceId = " ";
        Assert.Throws<ArgumentException>(() => SourceMediaPolicy.Validate(settings));
    }

    [Fact]
    public void StandbyProofWaitsForVideoAndCannotSurviveDeclarationChange()
    {
        var clock = new SupervisionClock(new ManualTimeProvider());
        var gate = new RecoveryReadinessGate(clock);
        var waiting = FfprobeStreamProbe.Parse("""{"streams":[{"codec_type":"video","codec_name":"h264","nb_read_frames":"0"},{"codec_type":"audio","codec_name":"aac","nb_read_frames":"48"}]}""");
        Assert.Null(gate.Poll("continuous", true, true, () => Task.FromResult(SourceMediaPolicy.Apply(waiting, new()))));
        gate.AccelerateForPublisherEpoch();
        var late = RecoveryReadinessTests.Av();
        var proof = gate.Poll("continuous", true, true, () => Task.FromResult(SourceMediaPolicy.Apply(late, new())));
        Assert.NotNull(proof);
        gate.Poll("audio-only", false, true, () => Task.FromResult(waiting));
        Assert.False(gate.TryAuthorizeHandoff(proof!));
    }
}
