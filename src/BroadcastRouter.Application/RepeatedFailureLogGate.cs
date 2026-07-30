namespace BroadcastRouter.Application;

public sealed record FailureLogDecision(bool ShouldLog, int SuppressedCount);

public sealed class RepeatedFailureLogGate
{
    private readonly object _gate = new();
    private string? _lastSignature;
    private DateTimeOffset _lastLoggedAt = DateTimeOffset.MinValue;
    private int _suppressed;

    public FailureLogDecision Evaluate(string signature, DateTimeOffset observedAt, TimeSpan repeatInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (repeatInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(repeatInterval));

        lock (_gate)
        {
            var signatureChanged = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
            if (signatureChanged || observedAt - _lastLoggedAt >= repeatInterval)
            {
                var decision = new FailureLogDecision(true, signatureChanged ? 0 : _suppressed);
                _lastSignature = signature;
                _lastLoggedAt = observedAt;
                _suppressed = 0;
                return decision;
            }

            _suppressed++;
            return new(false, _suppressed);
        }
    }

    public int Reset()
    {
        lock (_gate)
        {
            var suppressed = _suppressed;
            _lastSignature = null;
            _lastLoggedAt = DateTimeOffset.MinValue;
            _suppressed = 0;
            return suppressed;
        }
    }
}
