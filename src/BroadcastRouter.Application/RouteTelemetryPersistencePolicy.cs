using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RouteTelemetryPersistencePolicy
{
    public static bool RequiresPersistence(RuntimeRoute previous, RuntimeRoute current) =>
        previous.SourceId != current.SourceId
        || previous.SourceName != current.SourceName
        || previous.PortId != current.PortId
        || previous.PortName != current.PortName
        || previous.PresetId != current.PresetId
        || previous.State != current.State
        || previous.AssignmentMode != current.AssignmentMode
        || previous.Locked != current.Locked
        || previous.Priority != current.Priority
        || previous.RestartCount != current.RestartCount
        || previous.StartedAt != current.StartedAt
        || previous.FailureCategory != current.FailureCategory
        || previous.FailureMessage != current.FailureMessage
        || previous.RetryAt != current.RetryAt
        || previous.DesiredPortId != current.DesiredPortId
        || previous.DesiredPortName != current.DesiredPortName
        || previous.ReserveWhileOffline != current.ReserveWhileOffline
        || previous.AllowTemporaryUse != current.AllowTemporaryUse;
}
