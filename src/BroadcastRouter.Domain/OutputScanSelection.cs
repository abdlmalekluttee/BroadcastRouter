namespace BroadcastRouter.Domain;

public static class OutputScanSelection
{
    public const string Progressive = "progressive";
    public const string Interlaced = "interlaced";

    public static string Format(bool interlaced) => interlaced ? Interlaced : Progressive;

    public static bool TryParse(string? value, out bool interlaced)
    {
        if (string.Equals(value, Interlaced, StringComparison.Ordinal))
        {
            interlaced = true;
            return true;
        }

        if (string.Equals(value, Progressive, StringComparison.Ordinal))
        {
            interlaced = false;
            return true;
        }

        interlaced = false;
        return false;
    }

    public static string Describe(OutputPresetProfile preset)
    {
        var framesPerSecond = preset.FrameRateNumerator / (double)Math.Max(1, preset.FrameRateDenominator);
        var cadence = preset.Interlaced ? framesPerSecond * 2 : framesPerSecond;
        return $"{preset.Width}x{preset.Height} {cadence:0.###}{(preset.Interlaced ? "i" : "p")}";
    }

    public static string? StandardPresetMismatch(OutputPresetProfile preset)
    {
        var standard = FindStandard(preset);
        if (standard is null) return null;

        var matches = preset.Width == standard.Width
            && preset.Height == standard.Height
            && preset.FrameRateNumerator == standard.FrameRateNumerator
            && preset.FrameRateDenominator == standard.FrameRateDenominator
            && preset.Interlaced == standard.Interlaced;
        return matches
            ? null
            : $"Preset '{preset.Name}' is labelled {standard.DisplayName}, but its effective signal is {Describe(preset)}. Correct the raster, frame rate, or scan type before saving.";
    }

    public static bool TryApplyStandardPreset(OutputPresetProfile preset)
    {
        var standard = FindStandard(preset);
        if (standard is null) return false;
        preset.Width = standard.Width;
        preset.Height = standard.Height;
        preset.FrameRateNumerator = standard.FrameRateNumerator;
        preset.FrameRateDenominator = standard.FrameRateDenominator;
        preset.Interlaced = standard.Interlaced;
        return true;
    }

    private static StandardFormat? FindStandard(OutputPresetProfile preset) => StandardFormats.FirstOrDefault(format =>
        format.Token.Equals(Canonical(preset.Id), StringComparison.Ordinal)
        || format.Token.Equals(Canonical(preset.Name), StringComparison.Ordinal));

    private static string Canonical(string? value) => new((value ?? "")
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());

    private static readonly StandardFormat[] StandardFormats =
    [
        new("1080p25", "1080p25", 1920, 1080, 25, 1, false),
        new("1080p50", "1080p50", 1920, 1080, 50, 1, false),
        new("1080i50", "1080i50", 1920, 1080, 25, 1, true),
        new("720p50", "720p50", 1280, 720, 50, 1, false)
    ];

    private sealed record StandardFormat(string Token, string DisplayName, int Width, int Height,
        int FrameRateNumerator, int FrameRateDenominator, bool Interlaced);
}
