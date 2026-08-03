using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class MissingRouteProcessRecoveryPolicy
{
    public static bool RequiresRetry(bool sourceActive, RouteState state, bool ownsLiveProcess) =>
        sourceActive
        && !ownsLiveProcess
        && state is RouteState.Starting or RouteState.Running;
}
