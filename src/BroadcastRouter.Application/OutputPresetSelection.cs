using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class OutputPresetSelection
{
    public static void EnsureReferencesAvailable(
        IReadOnlyList<OutputPresetProfile> presets,
        IEnumerable<string> referencedPresetIds)
    {
        var available = presets.Select(preset => preset.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = referencedPresetIds.FirstOrDefault(presetId => !available.Contains(presetId));
        if (missing is not null)
            throw new InvalidOperationException($"Output preset '{missing}' is still used by an active or waiting route.");
    }

    public static OutputPresetProfile Resolve(
        IReadOnlyList<OutputPresetProfile> presets,
        string rulePresetId,
        string? requestedPresetId)
    {
        if (presets.Count == 0)
            throw new InvalidOperationException("At least one output preset is required before a route can start.");

        if (!string.IsNullOrWhiteSpace(requestedPresetId))
        {
            var normalized = requestedPresetId.Trim();
            return presets.FirstOrDefault(preset => preset.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"The selected output preset '{normalized}' is no longer available.");
        }

        return presets.FirstOrDefault(preset => preset.Id.Equals(rulePresetId, StringComparison.OrdinalIgnoreCase))
               ?? presets[0];
    }
}
