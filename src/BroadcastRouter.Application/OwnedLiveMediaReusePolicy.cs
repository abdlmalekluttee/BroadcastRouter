using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

/// <summary>
/// A running standard-video route already provides stronger continuous media
/// evidence than another short FFprobe sample. Reuse that confirmed media only
/// while its exact live owner is advancing; every other source remains probe-gated.
/// </summary>
public static class OwnedLiveMediaReusePolicy
{
    public static bool CanReuse(
        DiscoveredSource observed,
        DiscoveredSource? previous,
        bool liveOwnerRunning,
        long? frame,
        DateTimeOffset? lastProgressAt,
        DateTimeOffset now,
        TimeSpan progressFreshness)
    {
        if (progressFreshness <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(progressFreshness));
        return observed.State == SourceState.PublisherActive
            && observed.Media is null
            && previous is { State: SourceState.Ready, Media.HasUsableVideo: true }
            && liveOwnerRunning
            && frame > 0
            && lastProgressAt is { } progressAt
            && now >= progressAt
            && now - progressAt <= progressFreshness;
    }
}
