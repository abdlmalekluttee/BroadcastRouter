using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RoutePortOwnership
{
    public static IReadOnlyDictionary<string, RuntimeRoute> Map(
        IEnumerable<DeckLinkPort> ports,
        IEnumerable<RuntimeRoute> routes)
    {
        var knownPorts = ports.Select(port => port.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, RuntimeRoute>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in routes)
        {
            if (route.State == RouteState.Released) continue;
            AddFirstOwner(route.PortId, route);
            AddFirstOwner(route.DesiredPortId, route);
        }

        return owners;

        void AddFirstOwner(string? portId, RuntimeRoute route)
        {
            if (string.IsNullOrWhiteSpace(portId) || !knownPorts.Contains(portId)) return;
            owners.TryAdd(portId, route);
        }
    }
}
