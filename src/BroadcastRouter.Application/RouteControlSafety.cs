namespace BroadcastRouter.Application;

public static class RouteControlSafety
{
    public static void EnsureStartAllowed(bool emergencyStopped)
    {
        if (emergencyStopped)
            throw new InvalidOperationException("Emergency stop is active. Clear it explicitly before starting or reassigning a route.");
    }

    public static void EnsureStopAllowed(bool locked, bool forceRelease)
    {
        if (locked && !forceRelease)
            throw new InvalidOperationException("This assignment is locked. Unlock it or use emergency stop before releasing it.");
    }
}
