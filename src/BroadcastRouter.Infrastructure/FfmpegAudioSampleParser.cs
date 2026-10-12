using System.Globalization;

namespace BroadcastRouter.Infrastructure;

internal enum AudioSampleRecordState { Header, Sample, Invalid }
internal readonly record struct FfmpegAudioSample(long Samples, long Frame, long Pts, decimal PtsSeconds);

internal sealed class FfmpegAudioSampleParser
{
    private const string SampleKey = "lavfi.astats.Overall.Number_of_samples=";
    private FfmpegAudioSample? _pending;

    internal AudioSampleRecordState Accept(string line, out FfmpegAudioSample sample)
    {
        sample = default;
        var previous = _pending;
        _pending = null;
        if (line.Length is 0 or > 256) return AudioSampleRecordState.Invalid;
        if (line.StartsWith("frame:", StringComparison.Ordinal))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || !fields[1].StartsWith("pts:", StringComparison.Ordinal)
                || !fields[2].StartsWith("pts_time:", StringComparison.Ordinal)
                || !long.TryParse(fields[0].AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var frame)
                || !long.TryParse(fields[1].AsSpan(4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pts)
                || pts is long.MinValue or long.MaxValue
                || !decimal.TryParse(fields[2].AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return AudioSampleRecordState.Invalid;
            _pending = new(0, frame, pts, seconds);
            return previous is null ? AudioSampleRecordState.Header : AudioSampleRecordState.Invalid;
        }
        if (previous is { } header && line.StartsWith(SampleKey, StringComparison.Ordinal)
            && decimal.TryParse(line.AsSpan(SampleKey.Length), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var count)
            && count is > 0 and <= long.MaxValue && count == decimal.Truncate(count))
        {
            sample = header with { Samples = (long)count };
            return AudioSampleRecordState.Sample;
        }
        return AudioSampleRecordState.Invalid;
    }
}
