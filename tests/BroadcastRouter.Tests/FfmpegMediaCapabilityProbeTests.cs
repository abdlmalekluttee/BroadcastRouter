using System.Text.Json;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FfmpegMediaCapabilityProbeTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("ametadata", false)]
    public async Task DetailedValidationPublishesCapabilityWithoutBypassingHardwareValidation(string? missing, bool supported)
    {
        await using var fixture = new HelpFixture(missing, 0);
        var result = await MediaToolValidator.ValidateDetailedAsync(new()
            { FfmpegPath=fixture.Executable, FfprobePath=fixture.Executable }, CancellationToken.None);
        Assert.Equal(supported, result.Validation.IndependentMediaStatsSupported);
        Assert.False(result.Validation.CanStartHardwareRoutes);
    }

    [Theory]
    [InlineData(null, 0, true)]
    [InlineData("full", 0, false)]
    [InlineData("astats", 0, false)]
    [InlineData("ametadata", 0, false)]
    [InlineData(null, 37, false)]
    public async Task OnlyTheSelectedToolWithEveryCapabilityCanEnableObservers(string? missing, int exitCode, bool supported)
    {
        await using var fixture = new HelpFixture(missing, exitCode);
        Assert.Equal(supported, await FfmpegMediaStatsCapability.InspectAsync(fixture.Executable, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotBecomeASuccessfulCapabilityCheck()
    {
        await using var fixture = new HelpFixture(null, 0);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FfmpegMediaStatsCapability.InspectAsync(fixture.Executable, stop.Token));
    }

    private sealed class HelpFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "router-recovery-capability-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(_root, "BroadcastRouter.FakeFfmpeg.exe");
        internal HelpFixture(string? missing, int exitCode)
        {
            Directory.CreateDirectory(_root);
            // Support the isolated --artifacts-path build as well as the normal
            // CI layout; never accidentally run a stale child from another build.
            var isolated = Path.Combine(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.FullName,
                "BroadcastRouter.FakeFfmpeg", "release");
            var built = File.Exists(Path.Combine(isolated, "BroadcastRouter.FakeFfmpeg.exe")) ? isolated
                : Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
            foreach (var file in Directory.GetFiles(built, "BroadcastRouter.FakeFfmpeg.*"))
                File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
            var texts = new Dictionary<string, string>
            {
                ["full"]="-stats_enc_pre path\n-stats_enc_pre_fmt format\n-stats_mux_pre path\n-stats_mux_pre_fmt format",
                ["astats"]="Filter astats\nastats AVOptions:\n metadata <boolean> options\n measure_perchannel <flags> options\n none flags\n measure_overall <flags> options\n Number_of_samples flags",
                ["ametadata"]="Filter ametadata\nametadata AVOptions:\n mode <int> options\n print 4\n key <string> options\n file <string> options\n direct <boolean> options"
            };
            foreach (var (name,text) in texts)
                File.WriteAllText(Path.Combine(_root, $"help-{name}.json"), JsonSerializer.Serialize(new
                {
                    events=new[] { new { atMilliseconds=0, channel="stdout", text=name==missing ? "Unknown filter or option" : text } },
                    exitAfterMilliseconds=0, exitCode
                }));
        }
        public ValueTask DisposeAsync() => new(FixtureDirectory.DeleteAsync(_root));
    }
}
