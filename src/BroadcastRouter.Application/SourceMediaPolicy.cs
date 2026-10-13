using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SourceMediaPolicy
{
    public static SourceMediaProfile Get(OperatorSettings settings, string sourceId)
    {
        var profile = settings.SourceMediaProfiles.SingleOrDefault(p => p.SourceId == sourceId);
        return new() { SourceId = sourceId, Expectation = profile?.Expectation ?? SourceMediaExpectation.Undeclared,
            RequireAudio = profile?.RequireAudio ?? false };
    }

    public static StreamProbeResult Apply(StreamProbeResult probe, SourceMediaProfile profile, bool includeOptionalAudio = true)
    {
        if (!Enum.IsDefined(profile.Expectation))
            return probe with { FailureCategory = "Unknown", Detail = "Invalid source media expectation." };
        if (profile.Expectation == SourceMediaExpectation.Undeclared)
            return probe with { Media = probe.Media is null ? null : probe.Media with { Expectation = SourceMediaExpectation.Undeclared },
                FailureCategory = "Unknown", Detail = "Source media declaration is missing. Standby retained." };
        if (!probe.Opened || probe.Media is null) return probe;
        var audioOnly = profile.Expectation == SourceMediaExpectation.AudioOnly;
        var video = !audioOnly && probe.FramesReceived && probe.Media.HasUsableVideo;
        var media = probe.Media with { HasUsableVideo = video, Expectation = profile.Expectation };
        if (!audioOnly && !video)
            return probe with { FramesReceived = false, Media = media,
                FailureCategory = media.VideoCodec is null ? "Unknown" : "WaitingForKeyframe",
                Detail = media.VideoCodec is null ? "Required video track has not been observed. Standby retained."
                    : "Video track declared but no decoded frame yet. Standby retained." };
        if (profile.Expectation == SourceMediaExpectation.ContinuousVideo
            && (!double.IsFinite(probe.AdvancingVideoSeconds) || probe.AdvancingVideoSeconds < 1))
            return probe with { Media = media, FailureCategory = "Unknown",
                Detail = "Required one second of advancing decoded video timestamps has not been observed. Standby retained." };
        // Optional audio may be absent, but when selected it must actually
        // decode. Never open an output graph using packet-only audio evidence.
        if ((audioOnly || profile.RequireAudio || includeOptionalAudio && media.AudioCodec is not null)
            && (!probe.DecodedAudioReceived || media.AudioCodec is null
                || !double.IsFinite(probe.AdvancingAudioSeconds) || probe.AdvancingAudioSeconds < 1))
            return probe with { FramesReceived = video, Media = media, FailureCategory = "Unknown",
                Detail = "Required one second of advancing decoded audio samples has not been observed. Standby retained." };
        return probe with { FramesReceived = video, Media = media, FailureCategory = null,
            Detail = audioOnly ? "Declared audio-only source qualified; generated black selected for the next start."
                : "Declared source video qualified for the next start; no existing output graph changed." };
    }

    public static void Validate(OperatorSettings settings)
    {
        if (settings.SourceMediaProfiles is null || settings.SourceMediaProfiles.Any(p => p is null
                || string.IsNullOrWhiteSpace(p.SourceId) || p.SourceId != p.SourceId.Trim()
                || p.SourceId.Any(char.IsControl) || p.SourceId.Split('/').Length != 4
                || p.SourceId.Split('/').Any(string.IsNullOrEmpty) || !Enum.IsDefined(p.Expectation))
            || settings.SourceMediaProfiles.GroupBy(p => p.SourceId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Source media declarations require unique exact source identities and a supported expectation.");
    }
}
