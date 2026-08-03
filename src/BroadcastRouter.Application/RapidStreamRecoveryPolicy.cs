using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RapidStreamRecoveryPolicy
{
    public static bool CanAttemptReservedRecovery(RuntimeRoute route, DiscoveredSource? source) =>
        source is not null
        && source.State != SourceState.Disabled
        && (source.State == SourceState.Ready || DesiredRoutePolicy.HasSavedAssignment(route));

    public static bool IsEffectivelyActive(DiscoveredSource source, bool ownedLiveVideoIsAdvancing) =>
        source.State == SourceState.Ready
        || source.Media?.HasUsableVideo == true && ownedLiveVideoIsAdvancing;
}
