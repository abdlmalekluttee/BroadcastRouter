using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public enum SourceRouteActionKind { Start, Retry, View }

public static class SourceRouteActionPolicy
{
    public static SourceRouteActionKind Resolve(RuntimeRoute? route) => route?.State switch
    {
        RouteState.Failed => SourceRouteActionKind.Retry,
        RouteState.Reserved or RouteState.Starting or RouteState.Running or RouteState.Stalled
            or RouteState.Reconnecting or RouteState.Fallback or RouteState.WaitingForPort or RouteState.WaitingForStream
            => SourceRouteActionKind.View,
        _ => SourceRouteActionKind.Start
    };
}
