namespace BroadcastRouter.Application;

/// <summary>
/// Retain retry debt until one session has continuously advanced for 60 seconds.
/// This is not permission to restart or a substitute for track-health detection.
/// Unknown, interrupted or replaced observations restart the qualification window.
/// </summary>
public sealed class StableRecoveryBudget
{
    public static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(2);
    private Guid _session;
    private long _frames;
    private TimeSpan _mediaTime;
    private DateTimeOffset? _since;
    private DateTimeOffset _lastObserved;

    public bool Observe(Guid session, long uniqueFrames, TimeSpan mediaTime,
        DateTimeOffset progressAt, DateTimeOffset now, bool healthy)
    {
        var valid = healthy && uniqueFrames > 0 && mediaTime > TimeSpan.Zero
            && now >= progressAt && now - progressAt <= MaximumObservationGap;
        var continuous = valid && session == _session && uniqueFrames > _frames && mediaTime > _mediaTime
            && now >= _lastObserved && now - _lastObserved <= MaximumObservationGap;
        if (!continuous) _since = valid ? now : null;
        _since ??= valid ? now : null;
        _session = session;
        _frames = uniqueFrames;
        _mediaTime = mediaTime;
        _lastObserved = now;
        return continuous && _since is not null && now - _since.Value >= StableWindow;
    }
}
