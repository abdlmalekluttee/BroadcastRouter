namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegInputFailure(
    string Category,
    string Detail,
    DateTimeOffset DetectedAt);

public static class FfmpegInputFailureDetector
{
    public static bool TryClassify(string line, out string category)
    {
        category = "";
        if (string.IsNullOrWhiteSpace(line)) return false;

        var text = line.ToLowerInvariant();
        if (text.Contains("cseq ", StringComparison.Ordinal)
            && text.Contains(" expected", StringComparison.Ordinal)
            && text.Contains(" received", StringComparison.Ordinal))
        {
            category = "RtspProtocolDesynchronized";
            return true;
        }

        return false;
    }
}
