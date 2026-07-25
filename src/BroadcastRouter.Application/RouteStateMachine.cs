using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class RouteStateMachine
{
    private static readonly IReadOnlyDictionary<RouteState, HashSet<RouteState>> Allowed =
        new Dictionary<RouteState, HashSet<RouteState>>
        {
            [RouteState.Known] = [RouteState.PublisherActive, RouteState.Disabled, RouteState.Unavailable],
            [RouteState.PublisherActive] = [RouteState.Probing, RouteState.Unavailable],
            [RouteState.Probing] = [RouteState.Ready, RouteState.Unavailable, RouteState.Failed],
            [RouteState.Ready] = [RouteState.WaitingForPort, RouteState.Reserved, RouteState.Unavailable],
            [RouteState.WaitingForPort] = [RouteState.Reserved, RouteState.Unavailable, RouteState.Disabled],
            [RouteState.Reserved] = [RouteState.Starting, RouteState.Released],
            [RouteState.Starting] = [RouteState.Running, RouteState.Reconnecting, RouteState.Failed],
            [RouteState.Running] = [RouteState.Stalled, RouteState.Fallback, RouteState.Reconnecting, RouteState.Released],
            [RouteState.Stalled] = [RouteState.Reconnecting, RouteState.Fallback, RouteState.Failed],
            [RouteState.Reconnecting] = [RouteState.Probing, RouteState.Fallback, RouteState.Failed],
            [RouteState.Fallback] = [RouteState.Probing, RouteState.Released, RouteState.Failed],
            [RouteState.Unavailable] = [RouteState.Probing, RouteState.PublisherActive, RouteState.Disabled],
            [RouteState.Released] = [RouteState.Ready, RouteState.Disabled],
            [RouteState.Disabled] = [RouteState.Known],
            [RouteState.Failed] = [RouteState.Reconnecting, RouteState.Disabled]
        };

    public bool CanTransition(RouteState from, RouteState to) => from == to || Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public RouteRecord Transition(RouteRecord route, RouteState target)
    {
        if (!CanTransition(route.State, target))
            throw new InvalidOperationException($"Invalid route transition {route.State} -> {target} for {route.Source}.");
        return route with { State = target };
    }
}
