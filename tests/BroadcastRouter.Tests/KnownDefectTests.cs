using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

// Correctness assertions, intentionally failing until each finding's fixing PR.
// Never invert these assertions. The external CI validator requires the exact
// assertion message and test identity, and rejects unexpected passes/crashes.
[Trait("Category", "KnownDefect")]
public sealed class KnownDefectTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");

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

    [Fact, Trait("Finding", "R05")]
    public void DuplicateBurstRespectsConfirmationWindow()
    {
        var detector = new FfmpegInputFreezeDetector();
        detector.Observe(101, new(100, 25, TimeSpan.FromSeconds(4), 0, 0, 1, Now, false),
            Now, TimeSpan.FromSeconds(8));
        var later = Now.AddMilliseconds(500);
        var restart = detector.Observe(101, new(112, 25, TimeSpan.FromSeconds(4.5), 0, 12, 1, later, false),
            later, TimeSpan.FromSeconds(8));
        Assert.False(restart, "R05: a 500ms duplicate burst must not bypass the confirmation window");
    }

    [Fact, Trait("Finding", "R06")]
    public void FreshProgressDoesNotConcealStalledMedia()
    {
        var parser = new FfmpegProgressParser();
        foreach (var line in new[] { "frame=100", "out_time_us=4000000", "progress=continue" }) parser.Accept(line, Now);
        FfmpegProgressSnapshot? snapshot = null;
        foreach (var line in new[] { "frame=100", "out_time_us=4000000", "progress=continue" }) snapshot = parser.Accept(line, Now.AddHours(1));
        Assert.True(FfmpegStallDetector.IsStalled(true, snapshot, Now.AddHours(1), TimeSpan.FromSeconds(10)),
            "R06: fresh progress with unchanged media counters for one hour must be stalled");
    }

    [Fact, Trait("Finding", "R18")]
    public void DiagnosticActorIsRedacted() => Assert.False(AuditJson().Contains("PERSON_EXAMPLE", StringComparison.Ordinal),
        "R18: exported audit must not retain the raw actor");

    [Fact, Trait("Finding", "R18")]
    public void DiagnosticReasonIsRedacted() => Assert.False(AuditJson().Contains("FAKE_TEST_VALUE", StringComparison.Ordinal),
        "R18: exported audit must not retain the raw reason secret");

    private static string AuditJson() => JsonSerializer.Serialize(DiagnosticSanitizer.SanitizeConfigurationAudit(new[]
    {
        new ConfigurationAuditEntry(1, Now, "SyntheticTest", "synthetic-entity", "synthetic-card", "synthetic-port",
            "old", "new", "PERSON_EXAMPLE", "operator password=FAKE_TEST_VALUE", "ok")
    }));
}
