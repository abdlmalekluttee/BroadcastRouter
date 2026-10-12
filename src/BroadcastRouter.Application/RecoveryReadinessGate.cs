namespace BroadcastRouter.Application;

/// <summary>
/// Probe-only recovery. Poll never waits for I/O and never owns an output. One
/// request is in flight per source; stale publisher/config generations cannot
/// authorize a handoff. Failures retain a capped delay independently of routing.
/// </summary>
public sealed class RecoveryReadinessGate(SupervisionClock clock)
{
    private readonly object _gate = new();
    private static readonly int[] DelaysSeconds = [1, 2, 5, 10, 20, 30];
    public static readonly TimeSpan ProofLifetime = TimeSpan.FromSeconds(2);
    private Task<Observation>? _pending;
    private string? _signature;
    private long _generation;
    private long _pendingGeneration;
    private bool? _publisherConnected;
    private int _failures;
    private bool _acceleratedProbePending;
    private DateTimeOffset _nextProbe = DateTimeOffset.MinValue;
    private StreamProbeResult? _proof;
    private long _proofGeneration;
    private DateTimeOffset _proofCompletedAt;

    public void ObservePublisher(bool connected)
    {
        lock (_gate)
        {
            if (_publisherConnected != connected) _generation++;
            _publisherConnected = connected;
        }
    }

    public void Invalidate() { lock (_gate) _generation++; }

    public void AccelerateForPublisherEpoch()
    {
        lock (_gate)
        {
            _generation++;
            _nextProbe = clock.Now;
            _acceleratedProbePending = _pending is not null;
        }
    }

    public void ResetRetryBudget() { lock (_gate) _failures = 0; }

    public bool TryAuthorizeHandoff(StreamProbeResult proof)
    {
        lock (_gate)
        {
            var valid = ReferenceEquals(_proof, proof) && _proofGeneration == _generation
                && clock.Now - _proofCompletedAt <= ProofLifetime;
            _proof = null;
            return valid;
        }
    }

    public StreamProbeResult? Poll(string signature, bool? requireVideo, bool? requireAudio,
        Func<Task<StreamProbeResult>> probe)
    {
        lock (_gate)
        {
            if (_signature != signature)
            {
                _signature = signature;
                _generation++;
            }
            if (_pending is null)
            {
                if (clock.Now < _nextProbe) return null;
                _pendingGeneration = _generation;
                _pending = ObserveAsync(probe);
            }
            if (!_pending.IsCompleted) return null;
            var observation = _pending.GetAwaiter().GetResult(); // ObserveAsync captures all failures.
            _pending = null;
            var result = observation.Result;
            var videoExpected = requireVideo ?? result?.Media?.HasUsableVideo == true;
            var audioExpected = requireAudio ?? result?.Media?.AudioCodec is not null;
            var qualified = _pendingGeneration == _generation
                && clock.Now - observation.CompletedAt <= ProofLifetime
                && result is { Opened: true, Media: not null, FailureCategory: null }
                && (videoExpected || audioExpected)
                && (!videoExpected || result.FramesReceived && result.Media.HasUsableVideo)
                && (!audioExpected || result.DecodedAudioReceived);
            if (!qualified)
            {
                _failures = Math.Min(_failures + 1, DelaysSeconds.Length);
                _nextProbe = _acceleratedProbePending ? clock.Now : clock.Now.AddSeconds(DelaysSeconds[_failures - 1]);
                _acceleratedProbePending = false;
                return null;
            }
            // Consume each proof once. A failed start needs a new sample, never
            // the same successful sample replayed by another reconciliation path.
            _nextProbe = clock.Now.AddSeconds(30);
            _proof = result;
            _proofGeneration = _generation;
            _proofCompletedAt = observation.CompletedAt;
            return result;
        }
    }

    private async Task<Observation> ObserveAsync(Func<Task<StreamProbeResult>> probe)
    {
        try { return new(await probe().ConfigureAwait(false), clock.Now); }
        catch (Exception) { return new(null, clock.Now); } // Fault/cancel is not media proof.
    }

    private sealed record Observation(StreamProbeResult? Result, DateTimeOffset CompletedAt);
}
