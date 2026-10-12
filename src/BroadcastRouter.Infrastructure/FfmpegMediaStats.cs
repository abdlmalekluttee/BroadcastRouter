using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

internal readonly record struct FfmpegMediaObservation(MediaSignalKind Kind, long Counter,
    long? InputPts = null, long? TimeBaseNumerator = null, long? TimeBaseDenominator = null);

internal static class FfmpegMediaStats
{
    internal static bool TryParse(MediaSignalKind channel, string line, out FfmpegMediaObservation observation)
    {
        observation = default;
        return false;
    }
}
