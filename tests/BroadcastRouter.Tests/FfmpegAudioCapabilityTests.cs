using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegAudioCapabilityTests
{
    private const string Stats = "Filter astats\nastats AVOptions:\n"
        + " metadata <boolean> ..F.A...... inject metadata\n"
        + " measure_perchannel <flags> ..F.A...... select per channel\n"
        + " none ..F.A......\n"
        + " measure_overall <flags> ..F.A...... select overall\n"
        + " Number_of_samples ..F.A......\n";
    private const string Metadata = "Filter ametadata\nametadata AVOptions:\n"
        + " mode <int> ..F.A...... operation\n print 4 ..F.A...... print metadata\n"
        + " key <string> ..F.A...... select key\n file <string> ..F.A...... destination\n"
        + " direct <boolean> ..F.A...... reduce buffering\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteSuccessfulFilterHelpSupportsPcmObservation(bool stderr)
    {
        Assert.True(FfmpegMediaStatsCapability.SupportsAudioMetadata(Result(Stats, stderr), Result(Metadata, stderr)));
    }

    [Theory]
    [InlineData("metadata", true)]
    [InlineData("measure_perchannel", true)]
    [InlineData("none", true)]
    [InlineData("measure_overall", true)]
    [InlineData("Number_of_samples", true)]
    [InlineData("mode", false)]
    [InlineData("print", false)]
    [InlineData("key", false)]
    [InlineData("file", false)]
    [InlineData("direct", false)]
    public void MissingRequiredAudioFeatureRemainsUnsupported(string missing, bool stats)
    {
        var text = stats ? Stats : Metadata;
        var filtered = string.Join('\n', text.Split('\n').Where(line => line.Trim().Split(' ')[0] != missing));
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            Result(stats ? filtered : Stats), Result(stats ? Metadata : filtered)));
    }

    [Theory]
    [InlineData(false, 0, false, null)]
    [InlineData(true, 1, false, null)]
    [InlineData(true, null, false, null)]
    [InlineData(true, 0, true, null)]
    [InlineData(true, 0, false, "synthetic start failure")]
    public void FailedInvocationCannotAuthorizeAnAudioFilter(bool started, int? exit, bool timedOut, string? error)
    {
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            new(started, exit, Stats, "", timedOut, error), Result(Metadata)));
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            Result(Stats), new(started, exit, Metadata, "", timedOut, error)));
    }

    [Fact]
    public void ErrorMentionsAndWrongFilterNameAreNotAdvertisedCapabilities()
    {
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            Result(Stats.Replace("Filter astats", "Filter video_stats")), Result(Metadata)));
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            Result(Stats), Result(Metadata.Replace("Filter ametadata", "Filter metadata"))));
        Assert.False(FfmpegMediaStatsCapability.SupportsAudioMetadata(
            Result("Unsupported: metadata measure_perchannel none measure_overall Number_of_samples"),
            Result("Unsupported: mode print key file direct")));
    }

    private static ExternalCommandResult Result(string text, bool stderr = false) =>
        new(true, 0, stderr ? "" : text, stderr ? text : "", false, null);
}
