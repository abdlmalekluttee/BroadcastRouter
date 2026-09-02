namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Detects DeckLink starvation emitted when an FFmpeg session is still alive
/// but is no longer delivering usable video or audio. Startup warnings are
/// ignored while DeckLink primes its queues; after that grace period either
/// starvation warning is an actionable physical-output failure.
/// </summary>
public sealed class FfmpegMediaStarvationDetector
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(3);
    private DateTimeOffset? _firstAudioStarvationAt;
    private DateTimeOffset? _firstVideoStarvationAt;
    private int _audioStarvationCount;
    private int _videoStarvationCount;

    public bool Observe(string line, DateTimeOffset observedAt, DateTimeOffset processStartedAt,
        TimeSpan startupGrace, out string category, out string detail)
    {
        category = "";
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (observedAt - processStartedAt < startupGrace) return false;

        if (line.Contains("not enough buffered video frames", StringComparison.OrdinalIgnoreCase))
        {
            if (!Confirmed(ref _firstVideoStarvationAt, ref _videoStarvationCount, observedAt)) return false;
            category = "DeckLinkVideoStarved";
            detail = "DeckLink repeatedly reported that its video frame queue starved after startup.";
            return true;
        }
        if (line.Contains("no buffered audio", StringComparison.OrdinalIgnoreCase))
        {
            if (!Confirmed(ref _firstAudioStarvationAt, ref _audioStarvationCount, observedAt)) return false;
            category = "DeckLinkAudioStarved";
            detail = "DeckLink repeatedly reported that its audio queue starved after startup.";
            return true;
        }

        return false;
    }

    private static bool Confirmed(ref DateTimeOffset? firstAt, ref int count, DateTimeOffset observedAt)
    {
        if (firstAt is null || observedAt < firstAt || observedAt - firstAt > ConfirmationWindow)
        {
            firstAt = observedAt;
            count = 1;
            return false;
        }

        count++;
        return count >= 2;
    }
}
