using System.Text.RegularExpressions;

namespace BroadcastRouter.Infrastructure;

public sealed record FfmpegDiagnosticResult(
    bool ExecutableFound,
    string? VersionLine,
    bool HasDeckLinkOutput,
    string Detail,
    IReadOnlyList<DeckLinkSink> OutputDevices,
    string RawReport);

public sealed record DeckLinkSink(string FfmpegAddress, string DisplayName)
{
    public override string ToString() => $"{DisplayName} ({FfmpegAddress})";
}

public static partial class FfmpegDiagnostics
{
    public static async Task<FfmpegDiagnosticResult> InspectAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath)) return new(false, null, false, "FFmpeg executable was not found.", [], "Executable not found.");

        var version = await ExternalCommandRunner.RunAsync(executablePath, ["-version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var devices = await ExternalCommandRunner.RunAsync(executablePath, ["-hide_banner", "-devices"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var sinks = await ExternalCommandRunner.RunAsync(executablePath, ["-hide_banner", "-sinks", "decklink"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        var versionLine = FirstMeaningfulLine(version.CombinedOutput);
        var hasDeckLink = DeviceLine().IsMatch(devices.CombinedOutput);
        var outputDevices = ParseDeckLinkSinks(sinks.CombinedOutput);
        var report = BuildReport("ffmpeg -version", version) + Environment.NewLine + Environment.NewLine
            + BuildReport("ffmpeg -hide_banner -devices", devices) + Environment.NewLine + Environment.NewLine
            + BuildReport("ffmpeg -hide_banner -sinks decklink", sinks);

        string detail;
        if (!version.Started) detail = $"FFmpeg could not start: {version.StartError}";
        else if (version.ExitCode != 0) detail = $"FFmpeg starts but '-version' exits with code {version.ExitCode}. See raw report.";
        else if (!hasDeckLink) detail = "FFmpeg runs, but the DeckLink output device is not compiled in.";
        else if (outputDevices.Count == 0) detail = "DeckLink output is compiled in, but FFmpeg reported no DeckLink output devices.";
        else detail = $"DeckLink output is available and {outputDevices.Count} output device(s) were enumerated.";
        return new(true, versionLine, hasDeckLink, detail, outputDevices, report);
    }

    public static IReadOnlyList<DeckLinkSink> ParseDeckLinkSinks(string output)
    {
        var results = new List<DeckLinkSink>();
        var inDeckLinkList = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("sinks for decklink", StringComparison.OrdinalIgnoreCase)) { inDeckLinkList = true; continue; }
            if (!inDeckLinkList || line.StartsWith('[') || line.Contains("Cannot list", StringComparison.OrdinalIgnoreCase)) continue;
            line = line.TrimStart('*').Trim();
            var descriptionStart = line.LastIndexOf(" [", StringComparison.Ordinal);
            var description = descriptionStart > 0 ? line[(descriptionStart + 2)..].TrimEnd(']').Trim() : line;
            var address = (descriptionStart > 0 ? line[..descriptionStart] : line).Trim().Trim('\'', '"');
            description = description.Trim('\'', '"');
            if (address.Length > 0 && !results.Any(item => item.FfmpegAddress.Equals(address, StringComparison.OrdinalIgnoreCase)))
                results.Add(new DeckLinkSink(address, string.IsNullOrWhiteSpace(description) ? address : description));
        }
        return results;
    }

    private static string BuildReport(string command, ExternalCommandResult result)
    {
        var status = result.Started ? $"exit={result.ExitCode?.ToString() ?? "unknown"}{(result.TimedOut ? " timeout" : "")}" : $"start-failed: {result.StartError}";
        var output = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "<no console output>" : LogRedactor.Redact(result.CombinedOutput.Trim());
        return $"> {command}{Environment.NewLine}{status}{Environment.NewLine}{output}";
    }

    private static string? FirstMeaningfulLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    [GeneratedRegex(@"(?im)^\s*[D\.][E]\s+decklink\b")]
    private static partial Regex DeviceLine();
}
