using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class MediaEvidenceTracker(IEnumerable<MediaSignalKind> required, DateTimeOffset startedAt)
{
    public static readonly TimeSpan SuspectAfter = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StartupQualification = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly Dictionary<MediaSignalKind, Counter> _signals = required.Distinct().ToDictionary(k => k, _ => new Counter());
    private DateTimeOffset? _heartbeat;

    public void Observe(MediaSignalKind kind, long counter, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_signals.TryGetValue(kind, out var signal) || counter < 0
                || at < startedAt || at < signal.LastObservedAt) return;
            signal.LastObservedAt = at;
            if (signal.HighWater is not null && counter <= signal.HighWater) return;
            signal.HighWater = counter;
            if (signal.LastAdvance is null || at - signal.LastAdvance >= SuspectAfter)
                signal.RecoverySince = at;
            signal.LastAdvance = at;
            if (at - signal.RecoverySince >= RecoveryWindow) signal.EverQualified = true;
        }
    }

    public void Invalidate(MediaSignalKind kind, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_signals.TryGetValue(kind, out var signal) || at < signal.LastObservedAt) return;
            // Transport/schema loss is Unknown, not evidence that media failed.
            _signals[kind] = new Counter { LastObservedAt = at };
        }
    }

    public void ObserveHeartbeat(DateTimeOffset at)
    {
        lock (_gate) if (at >= startedAt && (_heartbeat is null || at > _heartbeat)) _heartbeat = at;
    }

    public MediaEvidenceSnapshot Snapshot(DateTimeOffset now, bool processAlive)
    {
        lock (_gate)
        {
            var signals = _signals.Select(pair => new MediaSignalEvidence(pair.Key, State(pair.Value, now), pair.Value.LastAdvance)).ToArray();
            // A measurement point counts once. Neither heartbeat nor management
            // observations can act as a second failed media signal.
            var corroborated = signals.Count(s => s.State == MediaSignalState.Failed) >= 2;
            return new(processAlive ? MediaSignalState.Healthy : MediaSignalState.Failed,
                _heartbeat is null ? MediaSignalState.Unknown : AgeState(now - _heartbeat.Value),
                signals, corroborated, !processAlive || corroborated);
        }
    }

    private MediaSignalState State(Counter signal, DateTimeOffset now)
    {
        if (!signal.EverQualified || signal.LastAdvance is null) return MediaSignalState.Unknown;
        var ageState = AgeState(now - signal.LastAdvance.Value);
        if (ageState == MediaSignalState.Failed && now - startedAt < StartupQualification)
            return MediaSignalState.Suspect;
        if (ageState == MediaSignalState.Healthy && signal.LastAdvance - signal.RecoverySince < RecoveryWindow)
            return MediaSignalState.Suspect;
        return ageState;
    }

    private static MediaSignalState AgeState(TimeSpan age) => age >= SuspectAfter + ConfirmationWindow
        ? MediaSignalState.Failed : age >= SuspectAfter ? MediaSignalState.Suspect : MediaSignalState.Healthy;

    private sealed class Counter
    {
        public long? HighWater;
        public DateTimeOffset LastObservedAt;
        public DateTimeOffset? LastAdvance;
        public DateTimeOffset? RecoverySince;
        public bool EverQualified;
    }
}
