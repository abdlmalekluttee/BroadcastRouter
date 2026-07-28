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
        var encoders = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var muxers = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-muxers"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var rtspOptions = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-h", "demuxer=rtsp"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var deckLinkOptions = await ExternalCommandRunner.RunAsync(paths.FfmpegPath, ["-hide_banner", "-h", "muxer=decklink"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var environment = await SystemEnvironmentScanner.ScanAsync(paths, cancellationToken).ConfigureAwait(false);

        var ffprobeVersion = ffprobe.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var requiredFilters = new[] { "scale", "fps", "yadif", "tinterlace", "setfield" };
        var missingFilters = requiredFilters.Where(filter => !ContainsTool(filters.CombinedOutput, filter)).ToArray();
        var hasPixelFormat = ContainsTool(pixelFormats.CombinedOutput, "uyvy422");
        var hasRawVideo = ContainsTool(codecs.CombinedOutput, "rawvideo");
        var hasRtspTimeout = rtspOptions.Started && rtspOptions.ExitCode == 0 && rtspOptions.CombinedOutput.Contains("-timeout", StringComparison.Ordinal);
        var hasWindowsDeckLinkSafeTerminate = deckLinkOptions.Started && deckLinkOptions.ExitCode == 0
            && deckLinkOptions.CombinedOutput.Contains("win_safe_terminate", StringComparison.Ordinal);
        var previewFilters = new[] { "scale", "pad", "overlay", "showvolume" };
        var missingPreviewFilters = previewFilters.Where(filter => !ContainsTool(filters.CombinedOutput, filter)).ToArray();
        var hasH264Encoder = ContainsTool(encoders.CombinedOutput, "libx264");
        var hasAacEncoder = ContainsTool(encoders.CombinedOutput, "aac");
        var hasMp4Muxer = ContainsTool(muxers.CombinedOutput, "mp4");
        var standbyFilters = new[] { "color", "smptebars", "smptehdbars", "testsrc2", "overlay", "drawtext" };
        var missingStandbyFilters = standbyFilters.Where(filter => !ContainsTool(filters.CombinedOutput, filter)).ToArray();
        var driver = environment.Findings.Any(x => x.StartsWith("PASS  Blackmagic Desktop Video", StringComparison.Ordinal));

        findings.Add(ffmpeg.ExecutableFound ? $"PASS: {ffmpeg.VersionLine}" : "FAIL: FFmpeg did not start.");
        findings.Add(ffprobe.Started && ffprobe.ExitCode == 0 ? $"PASS: {ffprobeVersion}" : "FAIL: FFprobe did not start successfully.");
        findings.Add(ffmpeg.HasDeckLinkOutput ? "PASS: DeckLink output is compiled into FFmpeg." : "FAIL: DeckLink output is not compiled into FFmpeg.");
        findings.Add(ffmpeg.OutputDevices.Count > 0 ? $"PASS: {ffmpeg.OutputDevices.Count} DeckLink output(s) enumerated." : "FAIL: No DeckLink outputs were enumerated.");
        findings.Add(missingFilters.Length == 0 ? "PASS: Required filters scale, fps, yadif, tinterlace, and setfield are available." : $"FAIL: Missing filters: {string.Join(", ", missingFilters)}.");
        findings.Add(hasRtspTimeout ? "PASS: The RTSP demuxer supports bounded socket timeouts." : "FAIL: The RTSP demuxer does not expose the required timeout option.");
        findings.Add(hasPixelFormat ? "PASS: uyvy422 pixel format is available." : "FAIL: uyvy422 pixel format is unavailable.");
        findings.Add(hasRawVideo ? "PASS: rawvideo codec support is available." : "FAIL: rawvideo codec support is unavailable.");
        findings.Add(hasWindowsDeckLinkSafeTerminate
            ? "PASS: Windows DeckLink safe-termination support is available and will be enabled for route outputs."
            : "WARN: This FFmpeg build does not expose the optional Windows DeckLink safe-termination workaround.");
        findings.Add(missingPreviewFilters.Length == 0 && hasH264Encoder && hasAacEncoder && hasMp4Muxer
            ? "PASS: Embedded preview filters, H.264/AAC encoders, and MP4 output are available."
            : $"WARN: Embedded preview is unavailable or incomplete. Missing: {string.Join(", ", missingPreviewFilters
                .Concat(hasH264Encoder ? [] : ["libx264 encoder"])
                .Concat(hasAacEncoder ? [] : ["AAC encoder"])
                .Concat(hasMp4Muxer ? [] : ["MP4 muxer"]))}.");
        findings.Add(missingStandbyFilters.Length == 0
            ? "PASS: Per-port color bars, logo overlay, labels, and synchronized clock filters are available."
            : $"WARN: Per-port standby screens are unavailable. Missing filters: {string.Join(", ", missingStandbyFilters)}.");
        findings.Add(driver ? "PASS: Blackmagic Desktop Video is installed." : "FAIL: Blackmagic Desktop Video was not detected.");

        var valid = ffmpeg.ExecutableFound && ffprobe.Started && ffprobe.ExitCode == 0 && ffmpeg.HasDeckLinkOutput
            && ffmpeg.OutputDevices.Count > 0 && missingFilters.Length == 0 && hasPixelFormat && hasRawVideo && hasRtspTimeout && driver;
        return new(valid ? ToolValidationState.Valid : ToolValidationState.Invalid, ffmpeg.VersionLine, ffprobeVersion,
            ffmpeg.HasDeckLinkOutput, driver, ffmpeg.OutputDevices.Count, findings, DateTimeOffset.UtcNow,
            hasWindowsDeckLinkSafeTerminate, missingStandbyFilters.Length == 0);
    }

    private static bool ContainsTool(string report, string token) =>
        report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase)));
}
