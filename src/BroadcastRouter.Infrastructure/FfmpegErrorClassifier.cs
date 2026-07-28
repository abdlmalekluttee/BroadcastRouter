namespace BroadcastRouter.Infrastructure;

public enum FfmpegFailureCategory
{
    None, Authentication, Network, RtspNotFound, Codec, UnsupportedMedia,
    DeckLinkUnavailable, DeckLinkBusy, DeckLinkFormat, DeckLinkInitialization,
    DeckLinkReference, Configuration, ProcessCrash, Unknown
}

public static class FfmpegErrorClassifier
{
    public static FfmpegFailureCategory Classify(int? exitCode, string standardError)
    {
        var text = standardError.ToLowerInvariant();
        if (ContainsAny(text, "401 unauthorized", "403 forbidden", "authentication failed")) return FfmpegFailureCategory.Authentication;
        if (ContainsAny(text, "404 not found", "server returned 404")) return FfmpegFailureCategory.RtspNotFound;
        if (ContainsAny(text, "device or resource busy", "already in use")) return FfmpegFailureCategory.DeckLinkBusy;
        if (text.Contains("decklink") && ContainsAny(text, "not found", "no such device", "unavailable")) return FfmpegFailureCategory.DeckLinkUnavailable;
        if (ContainsAny(text, "unsupported video mode", "display mode not supported", "pixel format is not supported")) return FfmpegFailureCategory.DeckLinkFormat;
        if (text.Contains("decklink") && ContainsAny(text, "genlock", "reference signal", "reference lost", "not locked")) return FfmpegFailureCategory.DeckLinkReference;
        if (text.Contains("decklink") && ContainsAny(text, "could not write header", "failed to start scheduled playback",
                "error sending frames", "not enough buffered video frames", "no buffered audio", "i/o error"))
            return FfmpegFailureCategory.DeckLinkInitialization;
        if (ContainsAny(text, "connection refused", "connection timed out", "network is unreachable", "no route to host", "i/o error")) return FfmpegFailureCategory.Network;
        if (ContainsAny(text, "decoder not found", "unknown decoder", "could not find codec parameters")) return FfmpegFailureCategory.Codec;
        if (ContainsAny(text, "invalid data found", "unsupported codec", "audio only")) return FfmpegFailureCategory.UnsupportedMedia;
        if (ContainsAny(text, "invalid argument", "unrecognized option", "option not found")) return FfmpegFailureCategory.Configuration;
        if (exitCode == 0) return FfmpegFailureCategory.None;
        if (exitCode is not null) return FfmpegFailureCategory.ProcessCrash;
        return FfmpegFailureCategory.Unknown;
    }

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(text.Contains);
}
