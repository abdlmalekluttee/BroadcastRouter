namespace BroadcastRouter.Infrastructure;

internal static class FfmpegMediaStatsCapability
{
    internal static async Task<bool> InspectAsync(string executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = await ExternalCommandRunner.RunAsync(executable, ["-hide_banner", "-h", "full"],
            TimeSpan.FromSeconds(10), cancellationToken, containOnWindows: true).ConfigureAwait(false);
        if (!SupportsRequiredOptions(full)) return false;
        var astats = await ExternalCommandRunner.RunAsync(executable, ["-hide_banner", "-h", "filter=astats"],
            TimeSpan.FromSeconds(10), cancellationToken, containOnWindows: true).ConfigureAwait(false);
        var metadata = await ExternalCommandRunner.RunAsync(executable, ["-hide_banner", "-h", "filter=ametadata"],
            TimeSpan.FromSeconds(10), cancellationToken, containOnWindows: true).ConfigureAwait(false);
        return SupportsAudioMetadata(astats, metadata);
    }

    internal static bool SupportsAudioMetadata(ExternalCommandResult astats, ExternalCommandResult metadata) =>
        HasFilterOptions(astats, "astats", ["metadata", "measure_perchannel", "none", "measure_overall", "Number_of_samples"])
        && HasFilterOptions(metadata, "ametadata", ["mode", "print", "key", "file", "direct"]);

    private static bool HasFilterOptions(ExternalCommandResult result, string filter, string[] required)
    {
        if (!result.Started || result.ExitCode != 0 || result.TimedOut || result.StartError is not null) return false;
        var lines = result.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!lines.Any(line => line.Trim() == $"Filter {filter}")
            || !lines.Any(line => line.Trim() == $"{filter} AVOptions:")) return false;
        var options = lines.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .ToHashSet(StringComparer.Ordinal);
        return required.All(options.Contains);
    }

    internal static bool SupportsRequiredOptions(ExternalCommandResult result)
    {
        if (!result.Started || result.ExitCode != 0 || result.TimedOut || result.StartError is not null) return false;
        var advertised = result.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(token => token is not null).ToHashSet(StringComparer.Ordinal);
        return new[] { "-stats_enc_pre", "-stats_mux_pre", "-stats_enc_pre_fmt", "-stats_mux_pre_fmt" }
            .All(option => advertised.Contains(option) || advertised.Contains(option + "[:<stream_spec>]"));
    }
}
