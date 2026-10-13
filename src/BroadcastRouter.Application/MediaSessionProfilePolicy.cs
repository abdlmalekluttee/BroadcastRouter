using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record MediaSessionProfile(long Epoch, long Generation, Guid SessionId, MediaProperties Media);

public sealed class MediaSessionProfilePolicy
{
    private readonly object _gate = new();
    private long _epoch;
    private long _generation;
    private bool _claimed;
    private MediaSessionProfile? _current;
    public MediaSessionProfile Attach(Guid session, MediaProperties media)
    {
        lock (_gate)
        {
            if (_current is { } current && current.SessionId == session && current.Epoch == _epoch
                && current.Generation == _generation) return current;
            return _current = new(_epoch, _generation, session, media);
        }
    }
    public void NewPublisherEpoch() { lock (_gate) { _epoch++; _generation++; _claimed = false; _current = null; } }
    public void InvalidateObservation() { lock (_gate) _generation++; }
    public bool IsCurrent(MediaSessionProfile session)
    {
        lock (_gate) return session == _current && session.Epoch == _epoch && session.Generation == _generation;
    }
    public bool CanObserveGrowth { get { lock (_gate) return !_claimed; } }
    public bool TryClaim(MediaSessionProfile session, StreamProbeResult probe, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_claimed || session != _current || session.Epoch != _epoch || session.Generation != _generation
                || session.Media.SelectedOverride != SourceMediaOverride.Auto
                || session.Media.HasUsableVideo && session.Media.AudioCodec is not null
                || duration < TimeSpan.FromSeconds(10)
                || probe is not { Opened: true, FramesReceived: true, DecodedAudioReceived: true, FailureCategory: null,
                    Media.HasUsableVideo: true, Media.AudioCodec: not null }
                || !double.IsFinite(probe.AdvancingVideoSeconds) || probe.AdvancingVideoSeconds < 10
                || !double.IsFinite(probe.AdvancingAudioSeconds) || probe.AdvancingAudioSeconds < 10
                || probe.MaximumVideoGapSeconds is not { } gap || !double.IsFinite(gap) || gap <= 0
                || gap > SourceMediaPolicy.ContinuousObservationMaximumGapSeconds)
                return false;
            _claimed = true;
            return true;
        }
    }
}
