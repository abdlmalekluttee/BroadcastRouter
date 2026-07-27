using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class DeckLinkIdentityMigration
{
    public static IReadOnlyList<DeckLinkPort> DeferUntilRestart(IEnumerable<DeckLinkPort> ports) => ports
        .Select(port => port.PreviousStableIds?.FirstOrDefault() is { Length: > 0 } previousId
            ? port with
            {
                StableId = previousId,
                IdentityConfidence = "Blackmagic persistent ID detected — safe migration is deferred until restart",
                PreviousStableIds = []
            }
            : port)
        .ToArray();

    public static IReadOnlyDictionary<string, string> BuildAliasMap(IEnumerable<DeckLinkPort> ports)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in ports)
        {
            foreach (var previousId in port.PreviousStableIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(previousId)
                    && !previousId.Equals(port.StableId, StringComparison.OrdinalIgnoreCase))
                    aliases.TryAdd(previousId, port.StableId);
            }
        }
        return aliases;
    }

    public static bool MigrateSettings(OperatorSettings settings, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyList<DeckLinkPort>? ports = null)
    {
        var changed = false;
        foreach (var mapping in settings.DeckLinkPortOverrides)
        {
            changed |= Rewrite(mapping.StableId, value => mapping.StableId = value, aliases);
            var currentPort = ports?.FirstOrDefault(port =>
                port.StableId.Equals(mapping.StableId, StringComparison.OrdinalIgnoreCase));
            if (currentPort?.FriendlyName is { Length: > 0 } detectedName
                && IsLegacyStatusName(mapping.FriendlyName))
            {
                mapping.FriendlyName = detectedName;
                changed = true;
            }
        }
        foreach (var source in settings.ManualSources)
            changed |= Rewrite(source.FixedPortId, value => source.FixedPortId = value, aliases);
        foreach (var rule in settings.Rules)
            changed |= Rewrite(rule.FixedPortId, value => rule.FixedPortId = value, aliases);

        if (settings.DeckLinkPortOverrides
            .GroupBy(mapping => mapping.StableId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            settings.DeckLinkPortOverrides = settings.DeckLinkPortOverrides
                .GroupBy(mapping => mapping.StableId, StringComparer.OrdinalIgnoreCase)
                .Select(MergeMappings)
                .ToList();
            changed = true;
        }
        return changed;
    }

    public static RuntimeRoute MigrateRoute(RuntimeRoute route, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, DeckLinkPort> ports)
    {
        if (route.PortId is null || !aliases.TryGetValue(route.PortId, out var stableId)) return route;
        ports.TryGetValue(stableId, out var port);
        return route with
        {
            PortId = stableId,
            PortName = port?.FriendlyName ?? port?.FfmpegName ?? route.PortName,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool Rewrite(string value, Action<string> assign, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(value) || !aliases.TryGetValue(value, out var stableId)) return false;
        assign(stableId);
        return true;
    }

    private static DeckLinkPortOverride MergeMappings(IGrouping<string, DeckLinkPortOverride> group)
    {
        var values = group.ToArray();
        var preferred = values.FirstOrDefault(value =>
            value.StableId.Equals(group.Key, StringComparison.Ordinal)) ?? values[0];
        return new DeckLinkPortOverride
        {
            StableId = group.Key,
            FriendlyName = values.Select(value => value.FriendlyName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? preferred.FriendlyName,
            PortGroup = values.Select(value => value.PortGroup).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? preferred.PortGroup,
            Reserved = values.Any(value => value.Reserved)
        };
    }

    private static bool IsLegacyStatusName(string value) =>
        value.EndsWith("] (none)", StringComparison.OrdinalIgnoreCase)
        && value.StartsWith("DeckLink", StringComparison.OrdinalIgnoreCase);
}
