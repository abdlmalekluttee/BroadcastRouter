using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class RouteStateMachine
{
    private static readonly IReadOnlyDictionary<RouteState, HashSet<RouteState>> Allowed =
        new Dictionary<RouteState, HashSet<RouteState>>
        {
            [RouteState.Known] = [RouteState.PublisherActive, RouteState.Probing, RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Unavailable, RouteState.Released, RouteState.Disabled, RouteState.Failed],
            [RouteState.PublisherActive] = [RouteState.Probing, RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Unavailable, RouteState.Released, RouteState.Disabled],
            [RouteState.Probing] = [RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Unavailable, RouteState.Released, RouteState.Failed],
            [RouteState.Ready] = [RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Unavailable, RouteState.Released, RouteState.Disabled],
            [RouteState.WaitingForPort] = [RouteState.Ready, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Unavailable, RouteState.Released, RouteState.Disabled, RouteState.Failed],
            [RouteState.Reserved] = [RouteState.Starting, RouteState.Reconnecting, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.Starting] = [RouteState.Running, RouteState.Reconnecting, RouteState.Fallback, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.Running] = [RouteState.Stalled, RouteState.Fallback, RouteState.Reconnecting, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.Stalled] = [RouteState.Reconnecting, RouteState.Fallback, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.Reconnecting] = [RouteState.Probing, RouteState.Reserved, RouteState.Starting, RouteState.Fallback, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.Fallback] = [RouteState.Probing, RouteState.Reserved, RouteState.Starting, RouteState.Reconnecting, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Released, RouteState.Failed],
            [RouteState.WaitingForStream] = [RouteState.Probing, RouteState.PublisherActive, RouteState.Ready, RouteState.WaitingForPort, RouteState.Reserved, RouteState.Released, RouteState.Disabled, RouteState.Failed],
            [RouteState.Unavailable] = [RouteState.Probing, RouteState.PublisherActive, RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Released, RouteState.Disabled],
            [RouteState.Released] = [RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Disabled],
            [RouteState.Disabled] = [RouteState.Known, RouteState.Released],
            [RouteState.Failed] = [RouteState.Reconnecting, RouteState.Ready, RouteState.WaitingForPort, RouteState.WaitingForStream, RouteState.Reserved, RouteState.Released, RouteState.Disabled]
        };

    public bool CanTransition(RouteState from, RouteState to) => from == to || Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public RouteRecord Transition(RouteRecord route, RouteState target)
    {
        if (!CanTransition(route.State, target))
            throw new InvalidOperationException($"Invalid route transition {route.State} -> {target} for {route.Source}.");
        return route with { State = target };
    }
}
