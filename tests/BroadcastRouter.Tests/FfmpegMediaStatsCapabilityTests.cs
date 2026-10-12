using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegMediaStatsCapabilityTests
{
    // Exact advertised help tokens; not a version-number allowlist. Accepting
    // a partial/failed help response would send unsupported flags to a route.
    private const string Help = "-stats_enc_pre[:<stream_spec>]  write encoding stats before encoding\n"
        + "-stats_mux_pre[:<stream_spec>]  write packets stats before muxing\n"
        + "-stats_enc_pre_fmt[:<stream_spec>]  format of encoding stats\n"
        + "-stats_mux_pre_fmt[:<stream_spec>]  format of muxing stats\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteSuccessfulHelpEnablesRequiredOptions(bool onStandardError)
    {
        var result = new ExternalCommandResult(true, 0, onStandardError ? "" : Help,
            onStandardError ? Help : "", false, null);
        Assert.True(FfmpegMediaStatsCapability.SupportsRequiredOptions(result));
    }

    [Theory]
    [InlineData("-stats_enc_pre[:<stream_spec>]")]
    [InlineData("-stats_mux_pre[:<stream_spec>]")]
    [InlineData("-stats_enc_pre_fmt[:<stream_spec>]")]
    [InlineData("-stats_mux_pre_fmt[:<stream_spec>]")]
    public void MissingOptionRemainsUnsupported(string missing)
    {
        var partial = string.Join('\n', Help.Split('\n').Where(line => !line.StartsWith(missing, StringComparison.Ordinal)));
        Assert.False(FfmpegMediaStatsCapability.SupportsRequiredOptions(new(true, 0, partial, "", false, null)));
    }

    [Theory]
    [InlineData(false, 0, false, null)]
    [InlineData(true, 1, false, null)]
    [InlineData(true, null, false, null)]
    [InlineData(true, 0, true, null)]
    [InlineData(true, 0, false, "synthetic start error")]
    public void FailedOrIncompleteInvocationCannotEnableFlags(bool started, int? exit, bool timedOut, string? error)
    {
        Assert.False(FfmpegMediaStatsCapability.SupportsRequiredOptions(new(started, exit, Help, "", timedOut, error)));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("Unsupported options: -stats_enc_pre -stats_mux_pre -stats_enc_pre_fmt -stats_mux_pre_fmt")]
    [InlineData("-stats_enc_pre_experimental\n-stats_mux_pre_experimental\n-stats_enc_pre_fmt_experimental\n-stats_mux_pre_fmt_experimental")]
    public void SubstringsAndDiagnosticMentionsAreNotCapabilities(string text)
    {
        Assert.False(FfmpegMediaStatsCapability.SupportsRequiredOptions(new(true, 0, text, "", false, null)));
    }
}
