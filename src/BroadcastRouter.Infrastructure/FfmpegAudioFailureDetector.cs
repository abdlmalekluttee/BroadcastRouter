namespace BroadcastRouter.Infrastructure;

/// <summary>
/// Detects sustained AAC decoder corruption and audio timestamp discontinuity
/// without treating one damaged packet as a route failure. This is intentionally
/// independent from DeckLink starvation so recovery can begin before the
/// physical output queue empties.
/// </summary>
public sealed class FfmpegAudioFailureDetector
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(5);
    private DateTimeOffset? _firstDecodeErrorAt;
    private DateTimeOffset? _firstTimestampErrorAt;
    private int _decodeErrorCount;
    private int _timestampErrorCount;

    public bool Observe(
        string line,
        DateTimeOffset observedAt,
        DateTimeOffset processStartedAt,
        TimeSpan startupGrace,
        out string category,
        out string detail)
    {
        category = "";
        detail = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (observedAt - processStartedAt < startupGrace) return false;

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
            if (!Confirmed(ref _firstDecodeErrorAt, ref _decodeErrorCount, observedAt)) return false;
            category = "AudioDecoderCorrupt";
            detail = "FFmpeg repeatedly rejected AAC packets in the owned live decoder session.";
            return true;
        }

        if (text.Contains("invalid dropping", StringComparison.Ordinal))
        {
            if (!Confirmed(ref _firstTimestampErrorAt, ref _timestampErrorCount, observedAt)) return false;
            category = "AudioTimestampDiscontinuous";
            detail = "FFmpeg repeatedly dropped invalid audio timestamps in the owned live decoder session.";
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
