namespace BroadcastRouter.Domain;

public enum MediaSignalState { Unknown, Healthy, Suspect, Failed }
public enum MediaSignalKind { InputVideo, InputAudio, OutputVideo, OutputAudio }

public sealed record MediaSignalEvidence(MediaSignalKind Kind, MediaSignalState State,
    DateTimeOffset? LastAdvancedAt);

public sealed record MediaEvidenceSnapshot(MediaSignalState Process, MediaSignalState Heartbeat,
    IReadOnlyList<MediaSignalEvidence> Signals, bool ConfirmedMediaFailure, bool MayRecover);
