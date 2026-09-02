namespace BroadcastRouter.Application;

public sealed record AuxiliaryLoopLivenessSnapshot(
    string Name,
    DateTimeOffset LastProgressAt,
    int RestartCount);

public static class AuxiliaryLoopLivenessPolicy
{
    public static bool IsResponsive(
        AuxiliaryLoopLivenessSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan maximumSilence)
    {
        if (maximumSilence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumSilence));

        return now <= snapshot.LastProgressAt || now - snapshot.LastProgressAt <= maximumSilence;
    }
}
