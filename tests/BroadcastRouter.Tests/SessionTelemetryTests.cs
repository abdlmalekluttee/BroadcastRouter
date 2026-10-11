using System.Reflection;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SessionTelemetryTests
{
    [Fact]
    public void RuntimeAudioUnderrunIsNotInitializationFailure()
    {
        var classify = typeof(FfmpegErrorClassifier).GetMethod("Classify", [typeof(int?), typeof(string), typeof(bool)]);
        Assert.NotNull(classify);
        var result = classify.Invoke(null, [null, "[decklink] No buffered audio", true]);
        Assert.Equal("DeckLinkRuntimeUnderrun", result!.ToString());
        Assert.Equal(FfmpegFailureCategory.DeckLinkInitialization,
            FfmpegErrorClassifier.Classify(null, "[decklink] No buffered audio"));
    }

    [Fact]
    public void NumericNotFoundExitIsNotAnUnexplainedCrash()
    {
        var fferrtag = -(0xF8 | ('4' << 8) | ('0' << 16) | ('4' << 24));
        Assert.Equal(-875574520, fferrtag);
        Assert.Equal(FfmpegFailureCategory.RtspNotFound, FfmpegErrorClassifier.Classify(fferrtag, ""));
    }

    [Fact]
    public void OwnedSessionHasExactProcessIdentityAndRetainedStderrReference()
    {
        foreach (var property in new[] { "SessionId", "ProcessStartUtc", "StderrLog" })
            Assert.True(typeof(RouteProcessSnapshot).GetProperty(property) is not null, "Missing owned-session property: " + property);
    }

    [Fact]
    public void DecisionRecordContainsReconstructableEvidence()
    {
        var record = typeof(FfmpegProcessSupervisor).Assembly.GetType("BroadcastRouter.Infrastructure.SupervisionDecisionRecord");
        Assert.NotNull(record);
        foreach (var property in new[] { "Utc", "MonotonicTicks", "ServiceRunId", "RouteId", "OutputId", "SourceEpoch",
            "SessionId", "ProcessId", "ProcessStartUtc", "Trigger", "Before", "After", "Threshold", "WindowMilliseconds",
            "SuppressedAlternatives", "Action", "Result", "StderrLog", "DecisionId" })
            Assert.True(record.GetProperty(property) is not null, "Missing decision evidence: " + property);
    }

    [Fact]
    public void RouterWarningsDoNotUseWindowsApplicationLog()
    {
        var program = File.ReadAllText(Path.Combine(FaultHarnessTests.RepositoryRoot(), "src", "BroadcastRouter.Web", "Program.cs"));
        Assert.DoesNotContain("builder.Logging.AddEventLog", program);
        Assert.Contains("RouterFileLoggerProvider", program);
        Assert.Contains("AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>", program);
    }
}
