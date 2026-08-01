namespace BroadcastRouter.Application;

public sealed record CoordinatorLivenessSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset LastProgressAt,
    DateTimeOffset? LastCompletedAt,
    string Stage,
    long CompletedCycles);

public static class CoordinatorLivenessPolicy
{
    public static bool IsResponsive(
        CoordinatorLivenessSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan maximumSilence)
    {
        if (maximumSilence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumSilence));

        // A backwards clock adjustment must not make a healthy worker look stale.
        return now <= snapshot.LastProgressAt || now - snapshot.LastProgressAt <= maximumSilence;
    }
}
