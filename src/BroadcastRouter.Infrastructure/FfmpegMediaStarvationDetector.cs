using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Detects DeckLink starvation emitted when an FFmpeg session is still alive
/// but is no longer delivering usable video or audio. Startup warnings are
/// ignored while DeckLink primes its queues. Warnings additionally require
/// independently failed input media at the matching measurement point.
/// A diagnostic category alone never authorizes the coordinator to stop a route.
/// </summary>
public sealed class FfmpegMediaStarvationDetector
{
    public bool Observe(string line, DateTimeOffset observedAt, DateTimeOffset processStartedAt,
        TimeSpan startupGrace, out string category, out string detail,
        MediaEvidenceSnapshot? media = null)
    {
        category = "";
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (observedAt - processStartedAt < startupGrace) return false;

        if (line.Contains("not enough buffered video frames", StringComparison.OrdinalIgnoreCase))
        {
            if (media?.Signals.Any(signal => signal.Kind == MediaSignalKind.InputVideo
                && signal.State == MediaSignalState.Failed && signal.LastAdvancedAt is { } advanced
                && observedAt - advanced >= MediaEvidenceTracker.SuspectAfter + MediaEvidenceTracker.ConfirmationWindow) != true)
                return false;
            category = "DeckLinkVideoStarved";
            detail = "DeckLink reported an empty video queue while decoded video advancement was confirmed failed.";
            return true;
        }
        if (line.Contains("no buffered audio", StringComparison.OrdinalIgnoreCase))
        {
            if (!FfmpegAudioFailureDetector.HasFailedPcm(media, observedAt)) return false;
            category = "DeckLinkAudioStarved";
            detail = "DeckLink reported an empty audio queue while decoded PCM advancement was confirmed failed.";
            return true;
        }

        return false;
    }

}
