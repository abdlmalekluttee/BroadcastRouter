using System.Globalization;

namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegProgressSnapshot(
    long Frame,
    double Fps,
    TimeSpan OutputTime,
    long DroppedFrames,
    long DuplicatedFrames,
    double Speed,
    DateTimeOffset LastProgressAt,
    bool Completed,
    DateTimeOffset? LastOutputAdvanceAt = null);

public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private long _maxFrame;
    private TimeSpan _maxOutputTime;
    private DateTimeOffset? _lastOutputAdvanceAt;

    public FfmpegProgressSnapshot? Accept(string line, DateTimeOffset observedAt)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return null;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        _values[key] = value;
        if (!key.Equals("progress", StringComparison.OrdinalIgnoreCase)) return null;

        var outputTime = TimeSpan.Zero;
        if (TryLong("out_time_us", out var microseconds)) outputTime = TimeSpan.FromTicks(microseconds * 10);
        else if (_values.TryGetValue("out_time", out var formatted)) TimeSpan.TryParse(formatted, CultureInfo.InvariantCulture, out outputTime);

        var frame = Long("frame");
        // Heartbeat arrival is not media advancement. Keep high-water marks so
        // repeated/regressing counters cannot refresh the output observation.
        if (frame > _maxFrame || outputTime > _maxOutputTime)
            _lastOutputAdvanceAt = observedAt;
        _lastOutputAdvanceAt ??= observedAt; // qualification baseline, not proof of a first frame
        _maxFrame = Math.Max(_maxFrame, frame);
        _maxOutputTime = outputTime > _maxOutputTime ? outputTime : _maxOutputTime;
        var snapshot = new FfmpegProgressSnapshot(
            frame,
            Double("fps"),
            outputTime,
            Long("drop_frames"),
            Long("dup_frames"),
            Speed("speed"),
            observedAt,
            value.Equals("end", StringComparison.OrdinalIgnoreCase),
            _lastOutputAdvanceAt);
        _values.Clear();
        return snapshot;
    }

    private long Long(string key) => TryLong(key, out var value) ? value : 0;
    private bool TryLong(string key, out long value) => long.TryParse(_values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private double Double(string key) => double.TryParse(_values.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private double Speed(string key)
    {
        var value = _values.GetValueOrDefault(key)?.TrimEnd('x');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) ? speed : 0;
    }
}

public static class FfmpegStallDetector
{
    public static bool IsStalled(bool processRunning, FfmpegProgressSnapshot? progress, DateTimeOffset now, TimeSpan timeout) =>
        processRunning && progress is not null && !progress.Completed
        && now - (progress.LastOutputAdvanceAt ?? progress.LastProgressAt) > timeout;

    public static bool IsFirstProgressTimedOut(bool processRunning, FfmpegProgressSnapshot? progress, DateTimeOffset startedAt,
        DateTimeOffset now, TimeSpan timeout) =>
        processRunning && (progress is null || progress.Frame <= 0) && timeout > TimeSpan.Zero && now - startedAt > timeout;
}

/// <summary>
/// Legacy type name: observes sustained duplicate-counter suspicion, not a
/// confirmed input freeze. FFmpeg's fps-filter repeats do not increment the
/// same counter as CFR synchronization. Neither counter proves decoder failure.
/// The coordinator must require independent media evidence before any restart.
/// </summary>
public sealed class FfmpegInputFreezeDetector(
    double minimumDuplicateRatio = 0.90,
    long minimumFrameAdvance = 5)
{
    private int? _processId;
    private long _frame;
    private long _duplicatedFrames;
    private DateTimeOffset? _duplicateDominatedSince;

    public bool Observe(int processId, FfmpegProgressSnapshot? progress, DateTimeOffset observedAt, TimeSpan timeout)
    {
        if (progress is null || progress.Completed || timeout <= TimeSpan.Zero)
        {
            Reset(processId, progress);
            return false;
        }

        if (_processId != processId || progress.Frame < _frame || progress.DuplicatedFrames < _duplicatedFrames)
        {
            Reset(processId, progress);
            return false;
        }

        var frameAdvance = progress.Frame - _frame;
        var duplicatedAdvance = progress.DuplicatedFrames - _duplicatedFrames;
        _frame = progress.Frame;
        _duplicatedFrames = progress.DuplicatedFrames;

        if (frameAdvance < minimumFrameAdvance) return false;
        var duplicateRatio = duplicatedAdvance / (double)frameAdvance;
        if (duplicatedAdvance <= 0 || duplicateRatio < minimumDuplicateRatio)
        {
            _duplicateDominatedSince = null;
            return false;
        }

        // Multiplicity cannot bypass elapsed observation. This result is only
        // diagnostic suspicion, never a Failed media measurement or restart vote.
        _duplicateDominatedSince ??= observedAt;
        return observedAt - _duplicateDominatedSince.Value >= timeout;
    }

    public void Reset() => Reset(null, null);

    private void Reset(int? processId, FfmpegProgressSnapshot? progress)
    {
        _processId = processId;
        _frame = progress?.Frame ?? 0;
        _duplicatedFrames = progress?.DuplicatedFrames ?? 0;
        _duplicateDominatedSince = null;
    }
}
