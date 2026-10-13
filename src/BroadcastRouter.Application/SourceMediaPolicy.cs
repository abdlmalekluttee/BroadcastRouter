using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class SourceMediaPolicy
{
    // Conservative observation boundary, not a promised source cadence. Sparse
    // evidence is shown as Unknown and cannot by itself prove a media fault.
    public const double ContinuousObservationMaximumGapSeconds = 0.25;
    public static SourceMediaProfile Get(OperatorSettings settings, string sourceId)
    {
        var profile = settings.SourceMediaProfiles.SingleOrDefault(p => p.SourceId == sourceId);
        return new() { SourceId = sourceId, Override = profile?.Override ?? SourceMediaOverride.Auto };
    }

    public static StreamProbeResult Apply(StreamProbeResult probe, SourceMediaProfile profile, bool includeOptionalAudio = true)
    {
        if (!Enum.IsDefined(profile.Override))
            return probe with { FailureCategory = "Unknown", Detail = "Invalid source media override." };
        if (!probe.Opened || probe.Media is null) return probe;
        var video = profile.Override switch {
            SourceMediaOverride.ForceAudioOnly => false,
            SourceMediaOverride.ForceAudioVideo or SourceMediaOverride.ForceVideoOnly => true,
            _ => probe.FramesReceived && probe.Media.HasUsableVideo };
        var audio = profile.Override switch {
            SourceMediaOverride.ForceVideoOnly => false,
            SourceMediaOverride.ForceAudioVideo or SourceMediaOverride.ForceAudioOnly => true,
            _ => probe.DecodedAudioReceived && probe.Media.AudioCodec is not null };
        var media = probe.Media with { HasUsableVideo = video && probe.FramesReceived,
            AudioCodec = audio ? probe.Media.AudioCodec : null,
            DetectedProfile = AutomaticMediaDetection.Profile(probe.FramesReceived && probe.Media.HasUsableVideo,
                probe.DecodedAudioReceived && probe.Media.AudioCodec is not null), SelectedOverride = profile.Override,
            Expectation = !video ? SourceMediaExpectation.AudioOnly
                : probe.AdvancingVideoSeconds < 1 || probe.MaximumVideoGapSeconds > ContinuousObservationMaximumGapSeconds
                    ? SourceMediaExpectation.SparseVideo : SourceMediaExpectation.ContinuousVideo };
        if (!video && !audio)
            return probe with { Media = media, FailureCategory = "Unknown",
                Detail = "No decoded input track observed. Standby retained." };
        if (video && (!probe.FramesReceived || !probe.Media.HasUsableVideo))
            return probe with { Media = media, FailureCategory = "WaitingForKeyframe",
                Detail = "Required video track has no decoded frame. Standby retained." };
        if (video && (!double.IsFinite(probe.AdvancingVideoSeconds) || probe.AdvancingVideoSeconds < 1))
            return probe with { Media = media, FailureCategory = "Unknown",
                Detail = "One second of advancing decoded video timestamps has not been observed. Standby retained." };
        // Qualification concerns the input, irrespective of the output mute.
        if (audio
            && (!probe.DecodedAudioReceived || media.AudioCodec is null
                || !double.IsFinite(probe.AdvancingAudioSeconds) || probe.AdvancingAudioSeconds < 1))
            return probe with { FramesReceived = video, Media = media, FailureCategory = "Unknown",
                Detail = "Required one second of advancing decoded audio samples has not been observed. Standby retained." };
        return probe with { FramesReceived = video, Media = media, FailureCategory = null,
            Detail = !video ? "Audio-only source qualified; generated black selected for the next start."
                : "Source video qualified for the next start; no existing output graph changed." };
    }

    public static void Validate(OperatorSettings settings)
    {
        if (settings.SourceMediaProfiles is null || settings.SourceMediaProfiles.Any(p => p is null
                || string.IsNullOrWhiteSpace(p.SourceId) || p.SourceId != p.SourceId.Trim()
                || p.SourceId.Any(char.IsControl) || p.SourceId.Split('/').Length != 4
                || p.SourceId.Split('/').Any(string.IsNullOrEmpty) || !Enum.IsDefined(p.Override))
            || settings.SourceMediaProfiles.GroupBy(p => p.SourceId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Source media overrides require unique exact source identities and a supported mode.");
    }
}
