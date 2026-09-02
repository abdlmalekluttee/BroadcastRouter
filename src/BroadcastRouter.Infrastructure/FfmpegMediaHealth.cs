namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegMediaHealthSnapshot(
    int AudioStarvationWarnings,
    int VideoStarvationWarnings,
    int AudioDecodeErrors,
    int AudioTimestampDiscontinuities,
    int VideoTimestampDiscontinuities,
    DateTimeOffset? FirstAnomalyAt,
    DateTimeOffset? LastAnomalyAt)
{
    public bool HasAnomalies => AudioStarvationWarnings > 0 || VideoStarvationWarnings > 0
        || AudioDecodeErrors > 0 || AudioTimestampDiscontinuities > 0
        || VideoTimestampDiscontinuities > 0;

    public string ToDiagnosticSummary()
    {
        if (!HasAnomalies) return "No classified media anomaly was captured.";
        return $"Media counters: audio-starvation={AudioStarvationWarnings}, "
            + $"video-starvation={VideoStarvationWarnings}, audio-decode-errors={AudioDecodeErrors}, "
            + $"audio-timestamp-discontinuities={AudioTimestampDiscontinuities}, "
            + $"video-timestamp-discontinuities={VideoTimestampDiscontinuities}, "
            + $"first={FirstAnomalyAt:O}, last={LastAnomalyAt:O}.";
    }
}

/// <summary>
/// Converts noisy FFmpeg stderr into bounded per-session counters. These
/// counters are diagnostic evidence, not content-level loudness measurements.
/// </summary>
public sealed class FfmpegMediaHealthTracker
{
    private readonly object _gate = new();
    private int _audioStarvation;
    private int _videoStarvation;
    private int _audioDecodeErrors;
    private int _audioTimestampDiscontinuities;
    private int _videoTimestampDiscontinuities;
    private DateTimeOffset? _firstAnomalyAt;
    private DateTimeOffset? _lastAnomalyAt;

    public void Observe(string line, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line.ToLowerInvariant();
        var audioStarved = text.Contains("no buffered audio", StringComparison.Ordinal);
        var videoStarved = text.Contains("not enough buffered video frames", StringComparison.Ordinal);
        var audioContext = text.Contains("[aac @", StringComparison.Ordinal)
            || text.Contains("/aac @", StringComparison.Ordinal)
            || text.Contains("aist#", StringComparison.Ordinal)
            || text.Contains("dec:aac", StringComparison.Ordinal);
        var videoContext = text.Contains("vist#", StringComparison.Ordinal)
            || text.Contains("dec:h264", StringComparison.Ordinal)
            || text.Contains("/h264 @", StringComparison.Ordinal);
        var decodeError = audioContext && (text.Contains("error submitting packet to decoder", StringComparison.Ordinal)
            || text.Contains("channel element", StringComparison.Ordinal)
            || text.Contains("reserved bit set", StringComparison.Ordinal)
            || text.Contains("number of bands", StringComparison.Ordinal)
            || text.Contains("scalefactor bands", StringComparison.Ordinal));
        var invalidTimestamp = text.Contains("invalid dropping", StringComparison.Ordinal);

        if (!audioStarved && !videoStarved && !decodeError
            && !(invalidTimestamp && (audioContext || videoContext))) return;

        lock (_gate)
        {
            if (audioStarved) _audioStarvation++;
            if (videoStarved) _videoStarvation++;
            if (decodeError) _audioDecodeErrors++;
            if (invalidTimestamp && audioContext) _audioTimestampDiscontinuities++;
            if (invalidTimestamp && videoContext) _videoTimestampDiscontinuities++;
            _firstAnomalyAt ??= observedAt;
            _lastAnomalyAt = observedAt;
        }
    }

    public FfmpegMediaHealthSnapshot Snapshot()
    {
        lock (_gate)
            return new(_audioStarvation, _videoStarvation, _audioDecodeErrors,
                _audioTimestampDiscontinuities, _videoTimestampDiscontinuities,
                _firstAnomalyAt, _lastAnomalyAt);
    }
}
