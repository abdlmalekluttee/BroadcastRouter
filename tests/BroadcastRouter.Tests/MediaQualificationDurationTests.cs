using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class MediaQualificationDurationTests
{
    [Theory]
    [InlineData("single-frame")]
    [InlineData("repeated-pts")]
    [InlineData("under-one-second")]
    [InlineData("missing-pts")]
    [InlineData("counter-only")]
    [InlineData("backward-pts")]
    public void ContinuousVideoNeedsOneSecondOfActualAdvancingPts(string defect)
    {
        var video = defect switch
        {
            "single-frame" => new[] { 0d },
            "repeated-pts" => Enumerable.Repeat(0d, 51).ToArray(),
            "under-one-second" => Enumerable.Range(0, 25).Select(i => i / 25d).ToArray(),
            "missing-pts" => new[] { double.NaN, double.NaN },
            "counter-only" => Array.Empty<double>(),
            "backward-pts" => new[] { 0d, 1d, 0.5d },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var result = SourceMediaPolicy.Apply(Probe(video, Audio()), new() { RequireAudio = true });
        Assert.Equal(SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(result));
        Assert.Contains("Standby retained", result.Detail);
    }

    [Theory]
    [InlineData(SourceMediaExpectation.ContinuousVideo)]
    [InlineData(SourceMediaExpectation.SparseVideo)]
    [InlineData(SourceMediaExpectation.AudioOnly)]
    public void DeclaredAudioNeedsOneSecondOfAdvancingSamples(SourceMediaExpectation expectation)
    {
        var result = SourceMediaPolicy.Apply(Probe(Video(), [(0, 1024)]), new() { Expectation = expectation, RequireAudio = true });
        Assert.Equal(SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(result));
        Assert.Contains("Standby retained", result.Detail);
    }

    [Theory]
    [InlineData("same-pts")]
    [InlineData("missing-samples")]
    [InlineData("timestamp-gap")]
    [InlineData("backward-pts")]
    public void AudioMetadataOrDiscontinuousEvidenceCannotQualify(string defect)
    {
        var audio = defect switch
        {
            "same-pts" => Enumerable.Repeat((0d, 1024), 60).ToArray(),
            "missing-samples" => Audio().Select(x => (x.Item1, 0)).ToArray(),
            "timestamp-gap" => new[] { (0d, 1024), (2d, 1024) },
            "backward-pts" => Audio().Append((0d, 1024)).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var result = SourceMediaPolicy.Apply(Probe(Video(), audio), new() { RequireAudio = true });
        Assert.Equal(SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(result));
    }

    [Fact]
    public void GenuineOneSecondAdvancementQualifiesWithoutRequiringPictureMotion()
    {
        var result = SourceMediaPolicy.Apply(Probe(Video(), Audio()), new() { RequireAudio = true });
        Assert.Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(result));
    }

    [Fact]
    public void UndeclaredSourceNeverSilentlyDefaultsToContinuous()
    {
        var result = SourceMediaPolicy.Apply(Probe(Video(), Audio()), SourceMediaPolicy.Get(new(), "TEST/live/default/new-source"));
        Assert.Equal(SourceState.Unknown, SourceProbeReadinessPolicy.Resolve(result));
        Assert.Contains("declaration", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneKeyframeThenStallKeepsStandbyAndRetriesBoundedly()
    {
        var time = new ManualTimeProvider();
        var gate = new RecoveryReadinessGate(new SupervisionClock(time));
        var calls = 0;
        for (var i = 0; i < 120; i++)
        {
            var proof = gate.Poll("fixed-declaration", true, true, () =>
            {
                calls++;
                return Task.FromResult(SourceMediaPolicy.Apply(Probe([0], Audio()), new() { RequireAudio = true }));
            });
            Assert.Null(proof);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.InRange(calls, 1, 12);
    }

    internal static double[] Video() => Enumerable.Range(0, 26).Select(i => i / 25d).ToArray();
    internal static (double, int)[] Audio() => Enumerable.Range(0, 48).Select(i => (i * 1024 / 48000d, 1024)).ToArray();
    internal static StreamProbeResult Probe(double[] video, (double, int)[] audio) => FfprobeStreamProbe.Parse(Json(video, audio));
    internal static string Json(double[] video, (double, int)[] audio) => JsonSerializer.Serialize(new
    {
        streams = new object[]
        {
            new { index = 0, codec_type = "video", codec_name = "h264", width = 1920, height = 1080, nb_read_frames = "50", avg_frame_rate = "25/1" },
            new { index = 1, codec_type = "audio", codec_name = "aac", sample_rate = "48000", channels = 2, nb_read_frames = "94" }
        },
        frames = video.Select(t => (object)new { media_type = "video", stream_index = 0, best_effort_timestamp_time = double.IsFinite(t) ? t.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null })
            .Concat(audio.Select(t => (object)new { media_type = "audio", stream_index = 1, best_effort_timestamp_time = t.Item1.ToString("R", System.Globalization.CultureInfo.InvariantCulture), nb_samples = t.Item2 })).ToArray()
    });
}
