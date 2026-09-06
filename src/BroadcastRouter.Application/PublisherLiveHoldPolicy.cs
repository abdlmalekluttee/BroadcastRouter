using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

/// <summary>Only authoritative observations change presence; API failures are not offline evidence.</summary>
public sealed class PublisherLiveHoldPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _connected = new(StringComparer.Ordinal);

    public void SeedConnected(string sourceId)
    {
        // A slow discovery result must never overwrite a newer fast-monitor observation.
        lock (_gate) _connected.TryAdd(sourceId, true);
    }

    public void Observe(string sourceId, bool connected)
    {
        lock (_gate) _connected[sourceId] = connected;
    }

    public bool ShouldHold(RuntimeRoute route, bool wowzaEnabled, bool ownedLiveProcessRunning)
    {
        lock (_gate) return route.HoldOutputWhilePublisherLive && wowzaEnabled
            && ownedLiveProcessRunning && _connected.GetValueOrDefault(route.SourceId);
    }
}
