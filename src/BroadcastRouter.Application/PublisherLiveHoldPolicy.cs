using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

/// <summary>Only authoritative observations change presence; API failures are not offline evidence.</summary>
public sealed class PublisherLiveHoldPolicy(SupervisionClock? clock = null)
{
    private readonly object _gate = new();
    private readonly SupervisionClock _clock = clock ?? SupervisionClock.System;
    public static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, (bool Connected, DateTimeOffset At)> _connected = new(StringComparer.Ordinal);

    public void SeedConnected(string sourceId)
    {
        // A slow discovery result must never overwrite a newer fast-monitor observation.
        lock (_gate) _connected.TryAdd(sourceId, (true, _clock.Now));
    }

    public void Observe(string sourceId, bool connected)
    {
        lock (_gate) _connected[sourceId] = (connected, _clock.Now);
    }

    public MediaSignalState Presence(string sourceId) => MediaSignalState.Unknown;

    public bool ShouldHold(RuntimeRoute route, bool wowzaEnabled, bool ownedLiveProcessRunning,
        MediaEvidenceSnapshot? media = null)
    {
        lock (_gate) return route.HoldOutputWhilePublisherLive && wowzaEnabled
            && ownedLiveProcessRunning && _connected.TryGetValue(route.SourceId, out var observation)
            && observation.Connected && _clock.Now - observation.At < EvidenceLifetime;
    }
}
