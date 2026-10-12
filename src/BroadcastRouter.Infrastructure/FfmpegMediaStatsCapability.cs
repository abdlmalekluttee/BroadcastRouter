namespace BroadcastRouter.Infrastructure;

internal static class FfmpegMediaStatsCapability
{
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
