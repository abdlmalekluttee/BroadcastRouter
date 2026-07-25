using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RouteStartFailureRecovery
{
    public static RuntimeRoute ReleaseAndFail(
        PortReservationManager reservations,
        RuntimeRoute route,
        SourceIdentity source,
        string failureMessage,
        DateTimeOffset now)
    {
        if (route.PortId is not null) reservations.Release(route.PortId, source, force: true);
        return route with
        {
            PortId = null,
            PortName = null,
            State = RouteState.Failed,
            FailureCategory = "ProcessStart",
            FailureMessage = failureMessage,
            RetryAt = null,
            UpdatedAt = now
        };
    }
}
