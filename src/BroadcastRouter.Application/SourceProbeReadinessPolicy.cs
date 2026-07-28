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
}
