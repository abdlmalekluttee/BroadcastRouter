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
    bool Completed);

public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

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

        var snapshot = new FfmpegProgressSnapshot(
            Long("frame"),
            Double("fps"),
            outputTime,
            Long("drop_frames"),
            Long("dup_frames"),
            Speed("speed"),
            observedAt,
            value.Equals("end", StringComparison.OrdinalIgnoreCase));
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
        processRunning && progress is not null && !progress.Completed && now - progress.LastProgressAt > timeout;

    public static bool IsFirstProgressTimedOut(bool processRunning, FfmpegProgressSnapshot? progress, DateTimeOffset startedAt,
        DateTimeOffset now, TimeSpan timeout) =>
        processRunning && (progress is null || progress.Frame <= 0) && timeout > TimeSpan.Zero && now - startedAt > timeout;
}
