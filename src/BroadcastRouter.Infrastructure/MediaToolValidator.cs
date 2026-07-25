using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public static class MediaToolValidator
{
    public static async Task<MediaToolValidation> ValidateAsync(MediaToolPaths paths, CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        if (!File.Exists(paths.FfmpegPath) || !File.Exists(paths.FfprobePath))
            return new(ToolValidationState.Invalid, null, null, false, false, 0,
                ["FFmpeg and FFprobe must both point to existing executable files."], DateTimeOffset.UtcNow);

        var ffmpeg = await FfmpegDiagnostics.InspectAsync(paths.FfmpegPath, cancellationToken).ConfigureAwait(false);
        var ffprobe = await ExternalCommandRunner.RunAsync(paths.FfprobePath, ["-version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var filters = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var pixelFormats = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-pix_fmts"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var codecs = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-codecs"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var environment = await SystemEnvironmentScanner.ScanAsync(paths, cancellationToken).ConfigureAwait(false);

        var ffprobeVersion = ffprobe.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var requiredFilters = new[] { "scale", "fps", "yadif" };
        var missingFilters = requiredFilters.Where(filter => !ContainsTool(filters.CombinedOutput, filter)).ToArray();
        var hasPixelFormat = ContainsTool(pixelFormats.CombinedOutput, "uyvy422");
        var hasRawVideo = ContainsTool(codecs.CombinedOutput, "rawvideo");
        var driver = environment.Findings.Any(x => x.StartsWith("PASS  Blackmagic Desktop Video", StringComparison.Ordinal));

        findings.Add(ffmpeg.ExecutableFound ? $"PASS: {ffmpeg.VersionLine}" : "FAIL: FFmpeg did not start.");
        findings.Add(ffprobe.Started && ffprobe.ExitCode == 0 ? $"PASS: {ffprobeVersion}" : "FAIL: FFprobe did not start successfully.");
        findings.Add(ffmpeg.HasDeckLinkOutput ? "PASS: DeckLink output is compiled into FFmpeg." : "FAIL: DeckLink output is not compiled into FFmpeg.");
        findings.Add(ffmpeg.OutputDevices.Count > 0 ? $"PASS: {ffmpeg.OutputDevices.Count} DeckLink output(s) enumerated." : "FAIL: No DeckLink outputs were enumerated.");
        findings.Add(missingFilters.Length == 0 ? "PASS: Required filters scale, fps, and yadif are available." : $"FAIL: Missing filters: {string.Join(", ", missingFilters)}.");
        findings.Add(hasPixelFormat ? "PASS: uyvy422 pixel format is available." : "FAIL: uyvy422 pixel format is unavailable.");
        findings.Add(hasRawVideo ? "PASS: rawvideo codec support is available." : "FAIL: rawvideo codec support is unavailable.");
        findings.Add(driver ? "PASS: Blackmagic Desktop Video is installed." : "FAIL: Blackmagic Desktop Video was not detected.");

        var valid = ffmpeg.ExecutableFound && ffprobe.Started && ffprobe.ExitCode == 0 && ffmpeg.HasDeckLinkOutput
            && ffmpeg.OutputDevices.Count > 0 && missingFilters.Length == 0 && hasPixelFormat && hasRawVideo && driver;
        return new(valid ? ToolValidationState.Valid : ToolValidationState.Invalid, ffmpeg.VersionLine, ffprobeVersion,
            ffmpeg.HasDeckLinkOutput, driver, ffmpeg.OutputDevices.Count, findings, DateTimeOffset.UtcNow);
    }

    private static bool ContainsTool(string report, string token) =>
        report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)));
}
