using System.Collections.Concurrent;

namespace BroadcastRouter.Application;

public sealed class StartupRouteRecoveryTracker
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    public void Track(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source ID is required.", nameof(sourceId));
        _pending[sourceId] = 0;
    }

    public bool IsPending(string sourceId) => _pending.ContainsKey(sourceId);

    public bool TryBegin(string sourceId) => _pending.TryRemove(sourceId, out _);
}
