using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Classifies a diagnostic only when decoded PCM has independently failed for
/// the media confirmation window. Warning count is not elapsed unhealthy time:
/// multiple diagnostics from one packet never supply additional failure votes.
/// </summary>
public sealed class FfmpegAudioFailureDetector
{
    public bool Observe(
        string line,
        DateTimeOffset observedAt,
        DateTimeOffset processStartedAt,
        TimeSpan startupGrace,
        out string category,
        out string detail,
        MediaEvidenceSnapshot? media = null)
    {
        category = "";
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (observedAt - processStartedAt < startupGrace) return false;
        if (!HasFailedPcm(media, observedAt)) return false;

        var text = line.ToLowerInvariant();
        var audioContext = text.Contains("[aac @", StringComparison.Ordinal)
            || text.Contains("/aac @", StringComparison.Ordinal)
            || text.Contains("aist#", StringComparison.Ordinal)
            || text.Contains("dec:aac", StringComparison.Ordinal);
        if (!audioContext) return false;

        var decoderCorruption = text.Contains("error submitting packet to decoder", StringComparison.Ordinal)
            || text.Contains("channel element", StringComparison.Ordinal)
            || text.Contains("reserved bit set", StringComparison.Ordinal)
            || text.Contains("number of bands", StringComparison.Ordinal)
            || text.Contains("scalefactor bands", StringComparison.Ordinal);
        if (decoderCorruption)
        {
            category = "AudioDecoderCorrupt";
            detail = "FFmpeg rejected AAC packets while decoded PCM advancement was confirmed failed.";
            return true;
        }

        if (text.Contains("invalid dropping", StringComparison.Ordinal))
        {
            category = "AudioTimestampDiscontinuous";
            detail = "FFmpeg dropped invalid audio timestamps while decoded PCM advancement was confirmed failed.";
            return true;
        }

        return false;
    }

    internal static bool HasFailedPcm(MediaEvidenceSnapshot? media, DateTimeOffset now) =>
        media?.Signals.Any(signal => signal.Kind == MediaSignalKind.InputAudio
            && signal.State == MediaSignalState.Failed && signal.LastAdvancedAt is { } advanced
            && now - advanced >= MediaEvidenceTracker.SuspectAfter + MediaEvidenceTracker.ConfirmationWindow) == true;
}
