using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class DesiredRoutePolicy
{
    public static bool HasSavedAssignment(RuntimeRoute? route) =>
        route is not null && !string.IsNullOrWhiteSpace(route.DesiredPortId)
        && route.AssignmentMode is AssignmentMode.Preconfigured or AssignmentMode.Manual
            or AssignmentMode.Fixed or AssignmentMode.Rule;

    public static int PriorityRank(AssignmentMode mode) => mode switch
    {
        AssignmentMode.Preconfigured or AssignmentMode.Fixed or AssignmentMode.Rule => 3,
        AssignmentMode.Manual => 2,
        AssignmentMode.Automatic => 1,
        _ => 0
    };

    public static bool ProtectsPortWhileOffline(RuntimeRoute route) =>
        HasSavedAssignment(route) && route.ReserveWhileOffline && !route.AllowTemporaryUse;

    public static RuntimeRoute MigrateLegacy(RuntimeRoute route)
    {
        if (!string.IsNullOrWhiteSpace(route.DesiredPortId) || string.IsNullOrWhiteSpace(route.PortId)
            || route.AssignmentMode == AssignmentMode.Automatic)
            return route;

        return route with
        {
            DesiredPortId = route.PortId,
            DesiredPortName = route.PortName,
            ReserveWhileOffline = true,
            AllowTemporaryUse = false
        };
    }

    public static RuntimeRoute ResetTransientStateForStartup(RuntimeRoute route, DateTimeOffset now)
    {
        if (!HasSavedAssignment(route)) return route;
        return route with
        {
            PortId = null,
            PortName = null,
            State = RouteState.WaitingForStream,
            Frame = null,
            Fps = null,
            Speed = null,
            StartedAt = null,
            RetryAt = null,
            UpdatedAt = now
        };
    }
}
