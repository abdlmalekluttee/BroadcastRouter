namespace BroadcastRouter.Infrastructure;

internal enum AudioSampleRecordState { Header, Sample, Invalid }
internal readonly record struct FfmpegAudioSample(long Samples, long Frame, long Pts, decimal PtsSeconds);

internal sealed class FfmpegAudioSampleParser
{
    internal AudioSampleRecordState Accept(string line, out FfmpegAudioSample sample)
    {
        sample = default;
        return AudioSampleRecordState.Invalid;
    }
}
