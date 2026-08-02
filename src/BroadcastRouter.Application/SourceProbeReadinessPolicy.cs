using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public enum SourceMediaMode { Unknown, Video, AudioLed }

public sealed record SourceMediaModeState(
    SourceMediaMode Mode,
    int ConsecutiveVideoProbes,
    int ConsecutiveAudioLedProbes,
    MediaProperties? LastUsableVideo)
{
    public static SourceMediaModeState Unknown { get; } = new(SourceMediaMode.Unknown, 0, 0, null);
}

public sealed record SourceMediaModeDecision(
    SourceMediaModeState State,
    StreamProbeResult EffectiveProbe,
    bool ModeChanged);

public static class SourceProbeReadinessPolicy
{
    public static bool NeedsExtendedVideoConfirmation(StreamProbeResult probe) =>
        probe.Opened
        && probe.AudioReceived
        && !probe.FramesReceived
        && probe.Media is { VideoCodec: not null, HasUsableVideo: false };

    public static StreamProbeResult PreferExtendedVideoEvidence(
        StreamProbeResult quickProbe,
        StreamProbeResult extendedProbe) =>
        extendedProbe.FramesReceived && extendedProbe.Media is { HasUsableVideo: true }
            ? extendedProbe
            : quickProbe;

    public static bool ShouldRestoreVideo(int consecutiveSustainedVideoProbes, int requiredProbes = 2) =>
        requiredProbes > 0 && consecutiveSustainedVideoProbes >= requiredProbes;

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

    public static SourceMediaModeDecision ObserveMediaMode(
        SourceMediaModeState previous,
        StreamProbeResult current,
        int videoConfirmationProbes = 2,
        int audioLedConfirmationProbes = 3)
    {
        if (videoConfirmationProbes < 1) throw new ArgumentOutOfRangeException(nameof(videoConfirmationProbes));
        if (audioLedConfirmationProbes < 1) throw new ArgumentOutOfRangeException(nameof(audioLedConfirmationProbes));

        var evidence = current.FramesReceived && current.Media is { HasUsableVideo: true }
            ? SourceMediaMode.Video
            : current.AudioReceived && current.Media is { AudioCodec: not null }
                ? SourceMediaMode.AudioLed
                : SourceMediaMode.Unknown;

        if (evidence == SourceMediaMode.Unknown)
            return new(previous with { ConsecutiveVideoProbes = 0, ConsecutiveAudioLedProbes = 0 }, current, false);

        if (previous.Mode == SourceMediaMode.Unknown)
        {
            var initialized = new SourceMediaModeState(evidence, 0, 0,
                evidence == SourceMediaMode.Video ? current.Media : previous.LastUsableVideo);
            return new(initialized, current, false);
        }

        if (previous.Mode == evidence)
        {
            var stable = previous with
            {
                ConsecutiveVideoProbes = 0,
                ConsecutiveAudioLedProbes = 0,
                LastUsableVideo = evidence == SourceMediaMode.Video ? current.Media : previous.LastUsableVideo
            };
            return new(stable, current, false);
        }

        if (previous.Mode == SourceMediaMode.AudioLed && evidence == SourceMediaMode.Video)
        {
            var confirmations = previous.ConsecutiveVideoProbes + 1;
            if (confirmations >= videoConfirmationProbes)
            {
                var restored = new SourceMediaModeState(SourceMediaMode.Video, 0, 0, current.Media);
                return new(restored, current, true);
            }

            var pending = previous with { ConsecutiveVideoProbes = confirmations, ConsecutiveAudioLedProbes = 0 };
            return new(pending, RetainAudioLedMode(current, previouslyAudioLed: true), false);
        }

        var audioConfirmations = previous.ConsecutiveAudioLedProbes + 1;
        if (audioConfirmations >= audioLedConfirmationProbes)
        {
            var audioLed = previous with
            {
                Mode = SourceMediaMode.AudioLed,
                ConsecutiveVideoProbes = 0,
                ConsecutiveAudioLedProbes = 0
            };
            return new(audioLed, current, true);
        }

        var waiting = previous with { ConsecutiveVideoProbes = 0, ConsecutiveAudioLedProbes = audioConfirmations };
        return new(waiting, RetainVideoMode(current, previous.LastUsableVideo), false);
    }

    private static StreamProbeResult RetainVideoMode(StreamProbeResult current, MediaProperties? lastUsableVideo)
    {
        if (!current.Opened || !current.AudioReceived || lastUsableVideo is not { HasUsableVideo: true })
            return current;

        var audio = current.Media;
        return current with
        {
            Media = lastUsableVideo with
            {
                AudioCodec = audio?.AudioCodec ?? lastUsableVideo.AudioCodec,
                AudioSampleRate = audio?.AudioSampleRate ?? lastUsableVideo.AudioSampleRate,
                AudioChannels = audio?.AudioChannels ?? lastUsableVideo.AudioChannels
            },
            FailureCategory = null,
            Detail = "Decoded-video mode retained while sparse-video observations await confirmation."
        };
    }
}
