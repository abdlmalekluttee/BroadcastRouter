using BroadcastRouter.Domain;
using System.Globalization;

namespace BroadcastRouter.Infrastructure;

internal readonly record struct FfmpegMediaObservation(MediaSignalKind Kind, long Counter,
    long? InputPts = null, long? TimeBaseNumerator = null, long? TimeBaseDenominator = null);

internal static class FfmpegMediaStats
{
    internal static bool TryParse(MediaSignalKind channel, string line, out FfmpegMediaObservation observation)
    {
        observation = default;
        if (line.Length is 0 or > 256 || !Enum.IsDefined(channel)) return false;
        var input = channel is MediaSignalKind.InputVideo or MediaSignalKind.InputAudio;
        var video = channel is MediaSignalKind.InputVideo or MediaSignalKind.OutputVideo;
        var fields = line.Split('|');
        if (fields.Length != (input ? 7 : 4) || fields[0] != "brmedia"
            || fields[1] != (input ? "pre" : "mux") || fields[2] != (video ? "v" : "a")
            || !Nonnegative(fields[3], out var counter)) return false;
        if (!input)
        {
            observation = new(channel, counter);
            return true;
        }
        // ni is the decoder frame identity, not the post-filter n counter.
        // Unavailable provenance is Unknown, never invented source advancement.
        if (!long.TryParse(fields[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pts)
            || pts is long.MinValue or long.MaxValue || !Nonnegative(fields[6], out _)) return false;
        var timebase = fields[5].Split('/');
        if (timebase.Length != 2 || !Nonnegative(timebase[0], out var numerator) || numerator == 0
            || !Nonnegative(timebase[1], out var denominator) || denominator == 0) return false;
        observation = new(channel, counter, pts, numerator, denominator);
        return true;
    }

    private static bool Nonnegative(string value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
}
