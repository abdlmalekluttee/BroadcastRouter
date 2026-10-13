using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AudioDetectorEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private const string DecodeError = "[aac @ 0x1] Error submitting packet to decoder";

    [Theory]
    [InlineData(MediaSignalState.Healthy, 6000, false)]
    [InlineData(MediaSignalState.Suspect, 6000, false)]
    [InlineData(MediaSignalState.Unknown, 6000, false)]
    [InlineData(MediaSignalState.Failed, -1, false)]
    [InlineData(MediaSignalState.Failed, 0, false)]
    [InlineData(MediaSignalState.Failed, 3999, false)]
    [InlineData(MediaSignalState.Failed, 4000, true)]
    [InlineData(MediaSignalState.Failed, 9000, true)]
    public void CurrentAudioDiagnosticsRequireFailedPcmAndElapsedTime(MediaSignalState state, int ageMs, bool expected)
    {
        var media = new MediaEvidenceSnapshot(MediaSignalState.Healthy, MediaSignalState.Healthy,
            [new(MediaSignalKind.InputAudio, state, Now.AddMilliseconds(-ageMs))], false, false);
        var decoder = new FfmpegAudioFailureDetector();
        var queue = new FfmpegMediaStarvationDetector();
        Assert.Equal(expected, decoder.Observe(DecodeError, Now, Now.AddMinutes(-1), TimeSpan.Zero,
            out var category, out _, media));
        if (expected) Assert.Equal("AudioDecoderCorrupt", category);
        Assert.Equal(expected, queue.Observe("No buffered audio", Now, Now.AddMinutes(-1), TimeSpan.Zero,
            out category, out _, media));
        if (expected) Assert.Equal("DeckLinkAudioStarved", category);
    }

    [Theory]
    [InlineData(MediaSignalKind.OutputAudio, true)]
    [InlineData(MediaSignalKind.InputVideo, true)]
    [InlineData(MediaSignalKind.InputAudio, false)]
    public void WrongMeasurementPointOrMissingTimestampCannotProveFailedPcm(MediaSignalKind kind, bool timestamp)
    {
        var media = new MediaEvidenceSnapshot(MediaSignalState.Healthy, MediaSignalState.Healthy,
            [new(kind, MediaSignalState.Failed, timestamp ? Now.AddMinutes(-1) : null)], false, false);
        var decoder = new FfmpegAudioFailureDetector();
        for (var i = 0; i < 4; i++)
            Assert.False(decoder.Observe(DecodeError, Now, Now.AddMinutes(-1), TimeSpan.Zero, out _, out _, media));
    }

    // Same correctness assertions as the strict R04 registry. They move to the
    // ordinary suite only in the fixing commit; the RED commit retains both.
    [Fact, Trait("Finding", "R04")]
    public void SamePacketAudioBurstDoesNotAuthorizeRecovery()
    {
        var detector = new FfmpegAudioFailureDetector();
        detector.Observe("[aac @ 0x1] channel element 1.0 is not allocated", Now,
            Now.AddMinutes(-1), TimeSpan.FromSeconds(5), out _, out _);
        var restart = detector.Observe("[aac @ 0x1] Error submitting packet to decoder", Now,
            Now.AddMinutes(-1), TimeSpan.FromSeconds(5), out _, out _);
        Assert.False(restart, "R04: two diagnostics from one packet must not authorize recovery");
    }

    [Fact, Trait("Finding", "R04")]
    public void MillisecondQueueWarningsDoNotAuthorizeRecovery()
    {
        var detector = new FfmpegMediaStarvationDetector();
        detector.Observe("No buffered audio", Now, Now.AddMinutes(-1), TimeSpan.FromSeconds(5), out _, out _);
        var restart = detector.Observe("No buffered audio", Now.AddMilliseconds(1),
            Now.AddMinutes(-1), TimeSpan.FromSeconds(5), out _, out _);
        Assert.False(restart, "R04: millisecond queue warnings alone must not authorize recovery");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(4000)]
    public void WarningMultiplicityWithoutPcmEvidenceNeverConfirmsAudioFailure(int separationMilliseconds)
    {
        var decoder = new FfmpegAudioFailureDetector();
        var queue = new FfmpegMediaStarvationDetector();
        var later = Now.AddMilliseconds(separationMilliseconds);
        decoder.Observe(DecodeError, Now, Now.AddMinutes(-1), TimeSpan.Zero, out _, out _);
        queue.Observe("No buffered audio", Now, Now.AddMinutes(-1), TimeSpan.Zero, out _, out _);
        Assert.False(decoder.Observe(DecodeError, later, Now.AddMinutes(-1), TimeSpan.Zero, out _, out _),
            "Warning multiplicity is not failed PCM advancement.");
        Assert.False(queue.Observe("No buffered audio", later, Now.AddMinutes(-1), TimeSpan.Zero, out _, out _),
            "Queue warnings are not failed PCM advancement.");
    }
}
