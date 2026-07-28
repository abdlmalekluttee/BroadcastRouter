using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SettingsConcurrencyPolicy
{
    public static void EnsureCurrent(long submittedRevision, long currentRevision)
    {
        if (submittedRevision == currentRevision) return;
        throw new InvalidOperationException(
            $"These settings are stale (screen revision {submittedRevision}, backend revision {currentRevision}). " +
            "Reload the page and reapply the change; no configuration was overwritten.");
    }

    public static OperatorSettings MarkApplied(OperatorSettings settings, long currentRevision,
        DateTimeOffset appliedAt, string appliedBy)
    {
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, 6);
        settings.ConfigurationRevision = checked(currentRevision + 1);
        settings.LastAppliedAt = appliedAt;
        settings.LastAppliedBy = string.IsNullOrWhiteSpace(appliedBy) ? "system" : appliedBy.Trim();
        return settings;
    }
}
