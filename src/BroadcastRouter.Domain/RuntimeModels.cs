namespace BroadcastRouter.Domain;

public enum ToolValidationState { NotConfigured, Validating, Valid, Invalid }

public sealed record MediaToolValidation(
    ToolValidationState State,
    string? FfmpegVersion,
    string? FfprobeVersion,
    bool DeckLinkCompiled,
    bool DesktopVideoInstalled,
    int DeckLinkOutputCount,
    IReadOnlyList<string> Findings,
    DateTimeOffset? CheckedAt)
{
    public bool CanStartHardwareRoutes => State == ToolValidationState.Valid && DeckLinkCompiled
        && DesktopVideoInstalled && DeckLinkOutputCount > 0;

    public static MediaToolValidation NotConfigured { get; } = new(
        ToolValidationState.NotConfigured, null, null, false, false, 0,
        ["Select ffmpeg.exe and ffprobe.exe in Settings > Media Tools."], null);
}

public sealed record RuntimeRoute(
    string SourceId,
    string SourceName,
    string? PortId,
    string? PortName,
    string PresetId,
    RouteState State,
    AssignmentMode AssignmentMode,
    bool Locked,
    int Priority,
    int RestartCount,
    long? Frame,
    double? Fps,
    double? Speed,
    long DroppedFrames,
    long DuplicatedFrames,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    string? FailureCategory,
    string? FailureMessage,
    DateTimeOffset? RetryAt = null);

public sealed record ServerHealth(
    string ServerId,
    string FriendlyName,
    bool Reachable,
    bool Authenticated,
    int ActiveStreamCount,
    string Summary,
    DateTimeOffset CheckedAt);

public sealed record StructuredLogEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? SourceId,
    string? CorrelationId);

public sealed record RouterSnapshot(
    IReadOnlyList<DiscoveredSource> Sources,
    IReadOnlyList<DeckLinkPort> Ports,
    IReadOnlyList<RuntimeRoute> Routes,
    IReadOnlyList<QueueItemSnapshot> Waiting,
    IReadOnlyList<ServerHealth> Servers,
    MediaToolValidation ToolValidation,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    double CpuPercent,
    long WorkingSetBytes,
    bool SimulationMode,
    bool EmergencyStopped,
    string StatusMessage);

public sealed record QueueItemSnapshot(string SourceId, int Priority, string Reason, long Sequence);
