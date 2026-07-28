using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SourceProbeReadinessPolicy
{
    public static SourceState Resolve(StreamProbeResult probe) =>
        probe.FramesReceived || probe.AudioReceived
            ? SourceState.Ready
            : probe.Opened
                ? SourceState.UnsupportedMedia
                : SourceState.RtspUnavailable;

    public static StreamProbeResult RetainAudioLedMode(StreamProbeResult current, bool previouslyAudioLed)
    {
        if (!previouslyAudioLed || !current.Opened || !current.AudioReceived
            || current.Media is not { AudioCodec: not null } media)
            return current;

        return current with
        {
            FramesReceived = false,
            Media = media with { HasUsableVideo = false },
            FailureCategory = null,
            Detail = "Audio-led mode retained so intermittent still frames cannot destabilize playout."
        };
    }
}
