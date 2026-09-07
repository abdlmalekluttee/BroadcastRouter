namespace BroadcastRouter.Application;

public static class SupervisionLoopPacing
{
    public static TimeSpan DelayAfterOverrun(TimeSpan period, TimeSpan elapsed)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        if (elapsed < period) return TimeSpan.Zero;
        return TimeSpan.FromTicks(Math.Max(1, period.Ticks / 4));
    }
}
