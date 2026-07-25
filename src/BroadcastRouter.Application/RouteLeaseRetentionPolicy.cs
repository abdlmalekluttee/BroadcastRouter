namespace BroadcastRouter.Application;

public static class RouteLeaseRetentionPolicy
{
    public static bool ShouldRelease(bool locked, DateTimeOffset missingSince, DateTimeOffset now, TimeSpan gracePeriod) =>
        !locked && gracePeriod >= TimeSpan.Zero && now - missingSince >= gracePeriod;

    public static bool IsStable(DateTimeOffset readySince, DateTimeOffset now, TimeSpan stablePeriod) =>
        stablePeriod <= TimeSpan.Zero || now - readySince >= stablePeriod;
}
