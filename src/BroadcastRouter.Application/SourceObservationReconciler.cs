using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SourceObservationReconciler
{
    public static IReadOnlyList<DiscoveredSource> FindStaleSources(
        IReadOnlyCollection<DiscoveredSource> existing,
        IReadOnlyCollection<DiscoveredSource> observations,
        IReadOnlyCollection<string> enabledServerIds,
        IReadOnlyCollection<string> successfullyPolledServerIds,
        bool simulationMode)
    {
        var observedIds = observations
            .Select(source => source.Identity.Value)
            .ToHashSet(StringComparer.Ordinal);
        var enabledIds = enabledServerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var successfulIds = successfullyPolledServerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return existing.Where(source =>
        {
            if (observedIds.Contains(source.Identity.Value)) return false;

            var serverId = source.Identity.ServerId;
            if (serverId.Equals("MANUAL", StringComparison.OrdinalIgnoreCase)) return true;
            if (serverId.Equals("SIM-WOWZA", StringComparison.OrdinalIgnoreCase)) return !simulationMode;
            if (!enabledIds.Contains(serverId)) return true;

            // A successful poll is authoritative. A failed poll is not: retain its
            // last healthy observations so transient network faults do not tear down routes.
            return successfulIds.Contains(serverId);
        }).ToArray();
    }
}
