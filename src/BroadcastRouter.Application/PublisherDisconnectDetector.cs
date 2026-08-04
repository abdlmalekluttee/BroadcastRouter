namespace BroadcastRouter.Application;

/// <summary>
/// Requires a small number of consecutive authoritative publisher-missing
/// observations before route recovery is requested. Successful observations
/// clear the pending count immediately.
/// </summary>
public sealed class PublisherDisconnectDetector(int requiredObservations = 2)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _missingCounts = new(StringComparer.Ordinal);

    public bool Observe(string sourceId, bool publisherConnected)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source identity is required.", nameof(sourceId));
        if (requiredObservations < 1) throw new ArgumentOutOfRangeException(nameof(requiredObservations));
        lock (_gate)
        {
            if (publisherConnected)
            {
                _missingCounts.Remove(sourceId);
                return false;
            }

            var count = _missingCounts.GetValueOrDefault(sourceId) + 1;
            _missingCounts[sourceId] = count;
            return count >= requiredObservations;
        }
    }

    /// <summary>
    /// Returns true once when a publisher comes back after a confirmed absence.
    /// The transition is consumed so a continuously connected publisher cannot
    /// bypass normal retry backoff after an early RTSP start failure.
    /// </summary>
    public bool ObserveConnected(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source identity is required.", nameof(sourceId));
        if (requiredObservations < 1) throw new ArgumentOutOfRangeException(nameof(requiredObservations));
        lock (_gate)
        {
            var confirmedReturn = _missingCounts.GetValueOrDefault(sourceId) >= requiredObservations;
            _missingCounts.Remove(sourceId);
            return confirmedReturn;
        }
    }

    public void Forget(string sourceId)
    {
        lock (_gate) _missingCounts.Remove(sourceId);
    }
}
