namespace BroadcastRouter.Application;

/// <summary>Per-output failure debt, serialized by the output gate. Launching a
/// process or changing its purpose never clears a failing device's circuit.</summary>
public sealed class StandbyRetryBudget
{
    private static readonly int[] Delays = [1, 2, 5, 10, 20, 30];
    private readonly Queue<DateTimeOffset> _failuresInWindow = new();
    private readonly StableRecoveryBudget _stable = new();
    private Guid _lastFailedSession;
    private int _failures;
    public bool CircuitOpen { get; private set; }
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public string? LastError { get; private set; }
    public bool CanAttempt(DateTimeOffset now) => now >= NextAttempt;

    public bool RecordFailure(Guid session, DateTimeOffset now, string detail, double jitter)
    {
        if (session == _lastFailedSession) return false;
        _lastFailedSession = session;
        LastError = detail;
        _failures = Math.Min(_failures + 1, Delays.Length);
        while (_failuresInWindow.TryPeek(out var at) && now - at > TimeSpan.FromSeconds(60))
            _failuresInWindow.Dequeue();
        _failuresInWindow.Enqueue(now);
        CircuitOpen |= _failuresInWindow.Count >= 3;
        // Positive jitter only: an open circuit must never retry sooner than 5m.
        NextAttempt = now.AddSeconds((CircuitOpen ? 300 : Delays[_failures - 1]) + Math.Clamp(jitter, 0, 1));
        return true;
    }

    public void ObserveProgress(Guid session, long frames, TimeSpan mediaTime,
        DateTimeOffset progressAt, DateTimeOffset now)
    {
        if (!_stable.Observe(session, frames, mediaTime, progressAt, now, healthy: true)) return;
        CircuitOpen = false;
        _failures = 0;
        _failuresInWindow.Clear();
        LastError = null;
        NextAttempt = DateTimeOffset.MinValue;
    }
}
