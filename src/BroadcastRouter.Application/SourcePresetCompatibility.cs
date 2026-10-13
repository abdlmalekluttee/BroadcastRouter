using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SourcePresetCompatibility
{
    public const string MutedAudioOnly = "incompatible preset: audio-only source on a muted output";

    // Warnings are advisory at save time. Admission uses the same rule, based on
    // fresh decoded evidence, so a saved incompatible assignment stays on standby.
    public static string? Warning(MediaProperties? media, bool includeAudio,
        SourceMediaOverride mode = SourceMediaOverride.Auto) => !includeAudio
        && (mode == SourceMediaOverride.ForceAudioOnly || media?.DetectedProfile == DetectedMediaProfile.AudioOnly
            || media is { HasUsableVideo: false, AudioCodec: not null }) ? MutedAudioOnly : null;
}
