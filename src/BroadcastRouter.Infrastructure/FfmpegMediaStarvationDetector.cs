namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Detects the paired DeckLink starvation signature emitted when an FFmpeg
/// session is still alive but is no longer delivering usable video or audio.
/// A single warning is deliberately non-fatal because DeckLink can emit one
/// transiently while its output queue is priming.
/// </summary>
public sealed class FfmpegMediaStarvationDetector
{
    private DateTimeOffset? _videoStarvedAt;
    private DateTimeOffset? _audioStarvedAt;

    public bool Observe(string line, DateTimeOffset observedAt, DateTimeOffset processStartedAt,
        TimeSpan startupGrace, TimeSpan pairingWindow, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;

        if (observedAt - processStartedAt < startupGrace)
        {
            _videoStarvedAt = null;
            _audioStarvedAt = null;
            return false;
        }

        ExpireOldObservations(observedAt, pairingWindow);
        if (line.Contains("not enough buffered video frames", StringComparison.OrdinalIgnoreCase))
            _videoStarvedAt = observedAt;
        if (line.Contains("no buffered audio", StringComparison.OrdinalIgnoreCase))
            _audioStarvedAt = observedAt;

        if (_videoStarvedAt is null || _audioStarvedAt is null
            || (_videoStarvedAt.Value - _audioStarvedAt.Value).Duration() > pairingWindow)
            return false;

        _videoStarvedAt = null;
        _audioStarvedAt = null;
        detail = "DeckLink reported simultaneous video-frame and audio-buffer starvation after startup.";
        return true;
    }

    private void ExpireOldObservations(DateTimeOffset observedAt, TimeSpan pairingWindow)
    {
        if (_videoStarvedAt is not null && observedAt - _videoStarvedAt.Value > pairingWindow)
            _videoStarvedAt = null;
        if (_audioStarvedAt is not null && observedAt - _audioStarvedAt.Value > pairingWindow)
            _audioStarvedAt = null;
    }
}
