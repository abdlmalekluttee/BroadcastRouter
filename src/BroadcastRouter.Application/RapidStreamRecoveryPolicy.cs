using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RapidStreamRecoveryPolicy
{
    public static bool ShouldSupervisePublisher(RuntimeRoute route) =>
        route.State is RouteState.Starting or RouteState.Running
        || CanAccelerateConnectedPublisherRecovery(route);

    public static bool CanAccelerateConnectedPublisherRecovery(RuntimeRoute route, bool ownsLiveProcess = true) =>
        DesiredRoutePolicy.HasSavedAssignment(route)
        && (route.State is RouteState.Reconnecting or RouteState.Fallback or RouteState.WaitingForStream
            || !ownsLiveProcess && route.State is RouteState.Starting or RouteState.Running);

    public static RuntimeRoute MarkConnectedPublisherRecoveryDue(
        RuntimeRoute route,
        DateTimeOffset observedAt,
        bool ownsLiveProcess) =>
        route with
        {
            State = !ownsLiveProcess && route.State is RouteState.Starting or RouteState.Running
                ? RouteState.Reconnecting
                : route.State,
            RetryAt = observedAt,
            FailureCategory = "PublisherRestored",
            FailureMessage = "Wowza confirmed that the publisher returned; reserved-route recovery was accelerated.",
            UpdatedAt = observedAt
        };

    public static DiscoveredSource MarkPublisherRestored(DiscoveredSource source, DateTimeOffset observedAt) =>
        source with
        {
            State = source.Media is null ? SourceState.PublisherActive : SourceState.Ready,
            LastObservedAt = observedAt
        };

    public static bool CanAttemptReservedRecovery(RuntimeRoute route, DiscoveredSource? source) =>
        source is not null
        && source.State != SourceState.Disabled
        && (source.State == SourceState.Ready || DesiredRoutePolicy.HasSavedAssignment(route));

    public static bool IsEffectivelyActive(DiscoveredSource source, bool ownedLiveVideoIsAdvancing) =>
        source.State == SourceState.Ready
        || source.Media?.HasUsableVideo == true && ownedLiveVideoIsAdvancing;

    public static bool ShouldKeepStartingAttempt(
        RuntimeRoute route,
        bool sourceIsEffectivelyActive,
        bool ownsStartingLiveProcess,
        DateTimeOffset processStartedAt,
        DateTimeOffset now,
        TimeSpan startupGrace) =>
        !sourceIsEffectivelyActive
        && DesiredRoutePolicy.HasSavedAssignment(route)
        && route.State == RouteState.Starting
        && ownsStartingLiveProcess
        && now - processStartedAt < startupGrace;

    public static bool ShouldEnterSavedRetry(RuntimeRoute route, bool sourceIsEffectivelyActive) =>
        !sourceIsEffectivelyActive
        && DesiredRoutePolicy.HasSavedAssignment(route)
        && route.State is RouteState.Starting or RouteState.Running;
}
