using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record ToolValidationContinuityDecision(
    MediaToolValidation EffectiveValidation,
    bool RetainedLastKnownGood,
    TimeSpan RetryAfter);

public static class ToolValidationContinuityPolicy
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SuccessRetry = TimeSpan.FromMinutes(5);

    public static ToolValidationContinuityDecision Decide(
        MediaToolValidation previous,
        MediaToolValidation candidate,
        DateTimeOffset? lastSuccessfulAt,
        DateTimeOffset now,
        bool operatorForced)
    {
        if (candidate.CanStartHardwareRoutes)
            return new(candidate, false, SuccessRetry);

        var withinGrace = lastSuccessfulAt is not null
            && now - lastSuccessfulAt.Value <= DefaultGrace;
        if (operatorForced || !previous.CanStartHardwareRoutes || !withinGrace)
            return new(candidate, false, FailureRetry);

        var findings = candidate.Findings
            .Concat(["WARN: The scheduled validation failed transiently; the last confirmed tool and hardware state remains active while validation retries."])
            .ToArray();
        var effective = previous with
        {
            Findings = findings,
            CheckedAt = candidate.CheckedAt
        };
        return new(effective, true, FailureRetry);
    }
}

public static class DeviceRediscoveryAuditPolicy
{
    public static bool ShouldAudit(IReadOnlyCollection<string> previousIds, IReadOnlyCollection<string> currentIds)
    {
        var previous = previousIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = currentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !previous.SetEquals(current);
    }
}

public static class DeckLinkReferencePollingPolicy
{
    public static readonly TimeSpan SuccessInterval = TimeSpan.FromSeconds(10);

    public static TimeSpan FailureDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 4);
        return TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << exponent)));
    }
}
