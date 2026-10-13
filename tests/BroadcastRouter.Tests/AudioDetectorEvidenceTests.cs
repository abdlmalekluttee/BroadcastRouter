using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class AudioDetectorEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    private const string DecodeError = "[aac @ 0x1] Error submitting packet to decoder";

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
