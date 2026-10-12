using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class MediaEvidenceTracker(IEnumerable<MediaSignalKind> required, DateTimeOffset startedAt)
{
    private readonly MediaSignalKind[] _required = required.Distinct().ToArray();
    public void Observe(MediaSignalKind kind, long counter, DateTimeOffset at) { }
    public void Invalidate(MediaSignalKind kind, DateTimeOffset at) { }
    public void ObserveHeartbeat(DateTimeOffset at) { }
    public MediaEvidenceSnapshot Snapshot(DateTimeOffset now, bool processAlive) =>
        new(MediaSignalState.Unknown, MediaSignalState.Unknown,
            _required.Select(k => new MediaSignalEvidence(k, MediaSignalState.Unknown, null)).ToArray(), false, false);
}
