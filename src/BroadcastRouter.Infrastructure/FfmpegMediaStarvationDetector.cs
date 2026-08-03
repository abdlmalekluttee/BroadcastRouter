namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Detects DeckLink starvation emitted when an FFmpeg session is still alive
/// but is no longer delivering usable video or audio. Startup warnings are
/// ignored while DeckLink primes its queues; after that grace period either
/// starvation warning is an actionable physical-output failure.
/// </summary>
public sealed class FfmpegMediaStarvationDetector
{
    public bool Observe(string line, DateTimeOffset observedAt, DateTimeOffset processStartedAt,
        TimeSpan startupGrace, out string category, out string detail)
    {
        category = "";
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (observedAt - processStartedAt < startupGrace) return false;

        if (line.Contains("not enough buffered video frames", StringComparison.OrdinalIgnoreCase))
        {
            category = "DeckLinkVideoStarved";
            detail = "DeckLink reported that its video frame queue starved after startup.";
            return true;
        }
        if (line.Contains("no buffered audio", StringComparison.OrdinalIgnoreCase))
        {
            category = "DeckLinkAudioStarved";
            detail = "DeckLink reported that its audio queue starved after startup.";
            return true;
        }

        return false;
    }
}
