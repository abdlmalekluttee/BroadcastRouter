using System.Diagnostics;
using Microsoft.Win32;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record SystemScanResult(MediaToolPaths DetectedTools, IReadOnlyList<string> Findings);

public static class SystemEnvironmentScanner
{
    public static Task<MediaToolPaths> DetectMediaToolsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var directories = CandidateDirectories();
        return new MediaToolPaths
        {
            FfmpegPath = FindExecutable("ffmpeg.exe", directories, cancellationToken) ?? "",
            FfprobePath = FindExecutable("ffprobe.exe", directories, cancellationToken) ?? ""
        };
    }, cancellationToken);

    public static async Task<SystemScanResult> ScanAsync(MediaToolPaths configured, CancellationToken cancellationToken)
    {
        var detected = await DetectMediaToolsAsync(cancellationToken).ConfigureAwait(false);
        var tools = new MediaToolPaths
        {
            FfmpegPath = ExistingOrDetected(configured.FfmpegPath, detected.FfmpegPath),
            FfprobePath = ExistingOrDetected(configured.FfprobePath, detected.FfprobePath),
            FfplayPath = configured.FfplayPath
        };
        var findings = new List<string>();
        await ReportToolAsync("FFmpeg", tools.FfmpegPath, findings, cancellationToken).ConfigureAwait(false);
        await ReportToolAsync("FFprobe", tools.FfprobePath, findings, cancellationToken).ConfigureAwait(false);

        if (File.Exists(tools.FfmpegPath))
        {
            var diagnostic = await FfmpegDiagnostics.InspectAsync(tools.FfmpegPath, cancellationToken).ConfigureAwait(false);
            findings.Add(diagnostic.HasDeckLinkOutput ? "PASS  FFmpeg DeckLink output device is compiled in." : "FAIL  FFmpeg has no DeckLink output device.");
            findings.Add(diagnostic.OutputDevices.Count > 0
                ? $"PASS  FFmpeg enumerated {diagnostic.OutputDevices.Count} DeckLink output(s):{Environment.NewLine}      {string.Join(Environment.NewLine + "      ", diagnostic.OutputDevices.Select(device => $"{device.FfmpegAddress} [{device.DisplayName}]"))}"
                : "FAIL  FFmpeg enumerated no DeckLink output devices.");
            findings.Add($"FFMPEG COMMAND REPORT{Environment.NewLine}{diagnostic.RawReport}");
        }

        findings.Add(InstalledProductContains("Wowza Streaming Engine") ? "PASS  Wowza Streaming Engine installation detected." : "WARN  Wowza Streaming Engine is not listed as installed.");
        findings.Add(InstalledProductContains("Blackmagic Desktop Video") || Directory.Exists(@"C:\Program Files\Blackmagic Design\Blackmagic Desktop Video")
            ? "PASS  Blackmagic Desktop Video installation detected."
            : "FAIL  Blackmagic Desktop Video installation was not detected.");
        return new(tools, findings);
    }

    private static async Task ReportToolAsync(string label, string path, List<string> findings, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) { findings.Add($"FAIL  {label} was not found."); return; }
        var result = await ExternalCommandRunner.RunAsync(path, ["-version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var version = result.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "<no console output>";
        findings.Add(result.Started && result.ExitCode == 0
            ? $"PASS  {label}: {path}{Environment.NewLine}      {version}"
            : $"FAIL  {label}: {path}{Environment.NewLine}      start={result.Started}, exit={result.ExitCode?.ToString() ?? "unknown"}, error={result.StartError ?? version}");
    }

    private static List<string> CandidateDirectories()
    {
        var result = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg") };
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path)) result.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    private static string? FindExecutable(string fileName, IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { var candidate = Path.Combine(directory, fileName); if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch (Exception) when (directory.Length > 0) { }
        }

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(packages, fileName, SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return candidate;
            }
        }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool InstalledProductContains(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
        using (var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            using (var product = uninstall.OpenSubKey(name))
                if (product?.GetValue("DisplayName") is string display && display.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ExistingOrDetected(string configured, string detected) => File.Exists(configured) ? configured : detected;
}
