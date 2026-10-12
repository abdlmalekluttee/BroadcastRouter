using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FaultHarnessTests
{
    [Theory]
    [InlineData("progress")]
    [InlineData("stall")]
    [InlineData("stderr")]
    [InlineData("exit")]
    [InlineData("hang-stop")]
    [InlineData("crash")]
    public async Task ScriptableChildExercisesRealSupervisor(string scenario)
    {
        var root = RepositoryRoot();
        var executable = Path.Combine(root, "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe");
        Assert.True(File.Exists(executable), "Scriptable fake FFmpeg child is missing");
        var source = new SourceIdentity("TEST", "fixture", "_default_", "synthetic");
        var script = Path.Combine(Path.GetTempPath(), "router-child-" + Guid.NewGuid().ToString("N") + ".json");
        var events = scenario is "progress" or "stall"
            ? new[] { new { atMilliseconds = 0, channel = "stdout", text = "frame=1\nout_time_us=40000\nprogress=continue" } }
            : new[] { new { atMilliseconds = 0, channel = "stderr", text = "synthetic stderr evidence" } };
        await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new
        {
            events, exitAfterMilliseconds = scenario is "exit" or "crash" ? 150 : (int?)null,
            exitCode = scenario == "exit" ? 37 : 0, crash = scenario == "crash", ignoreQuit = scenario == "hang-stop"
        }));
        await using var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused"), TimeSpan.FromMilliseconds(100));
        var lifecycle = new System.Collections.Concurrent.ConcurrentQueue<RouteProcessLifecycleEvent>();
        supervisor.LifecycleChanged += lifecycle.Enqueue;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            await supervisor.StartOwnedProcessForTestingAsync(source, start);
            await Eventually(() => supervisor.Snapshot().Any(p => scenario is "progress" or "stall"
                ? p.Progress?.Frame == 1 : p.RecentErrors.Any(e => e.Contains("synthetic stderr evidence"))));
            if (scenario is "exit" or "crash")
            {
                await Eventually(() => lifecycle.Any(e => e.State == RouteProcessLifecycleState.Exited));
                if (scenario == "exit") Assert.Equal(37, supervisor.Snapshot().Single().ExitCode);
                else Assert.NotEqual(0, supervisor.Snapshot().Single().ExitCode);
            }
            else
            {
                if (scenario == "stall")
                {
                    var snapshot = supervisor.Snapshot().Single();
                    Assert.True(FfmpegStallDetector.IsStalled(true, snapshot.Progress,
                        snapshot.Progress!.LastProgressAt.AddSeconds(11), TimeSpan.FromSeconds(10)));
                }
                await supervisor.StopAsync(source, CancellationToken.None);
                Assert.False(supervisor.Snapshot().Single().Running);
                Assert.Equal(scenario == "hang-stop", lifecycle.Any(e => e.State == RouteProcessLifecycleState.ForcedTermination));
            }
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public void FakeRestImplementationExists()
    {
        Assert.NotNull(typeof(FaultHarnessTests).Assembly.GetType("BroadcastRouter.Tests.ScriptedWowzaHandler"));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task ScriptedRestErrorsReachDiscoveryWithoutNetwork(int status)
    {
        using var handler = new ScriptedWowzaHandler(new RestStep((HttpStatusCode)status, "synthetic management fault"));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Discovery(client).DiscoverAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ScriptedTimeoutObeysDiscoveryCancellation()
    {
        using var handler = new ScriptedWowzaHandler(new RestStep(HttpStatusCode.OK, "", Timeout: true));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery(client).DiscoverAsync(CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ScriptedPublisherEpochCanDisconnectAndReturn()
    {
        using var handler = new ScriptedWowzaHandler(ScriptedWowzaHandler.Publisher(true, 100),
            ScriptedWowzaHandler.Publisher(false, 0), ScriptedWowzaHandler.Publisher(true, 1));
        using var client = new HttpClient(handler);
        var provider = Discovery(client);
        Assert.Equal(SourceState.PublisherActive, (await provider.DiscoverAsync(CancellationToken.None)).Single().State);
        Assert.Equal(SourceState.PublisherDisconnected, (await provider.DiscoverAsync(CancellationToken.None)).Single().State);
        Assert.Equal(SourceState.PublisherActive, (await provider.DiscoverAsync(CancellationToken.None)).Single().State);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task ScriptedTruncatedJsonIsNotAValidInventory()
    {
        using var handler = new ScriptedWowzaHandler(new RestStep(HttpStatusCode.OK, "{\"incomingStreams\":["));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<JsonException>(() => Discovery(client).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScriptedMissingEnvelopeReachesRealParser()
    {
        using var handler = new ScriptedWowzaHandler(new RestStep(HttpStatusCode.OK, "{\"unexpectedEnvelope\":true}"));
        using var client = new HttpClient(handler);
        using var document = JsonDocument.Parse("{\"unexpectedEnvelope\":true}");
        Assert.Throws<JsonException>(() => WowzaIncomingStreamParser.Parse(document.RootElement));
        await Assert.ThrowsAsync<JsonException>(() => Discovery(client).DiscoverAsync(CancellationToken.None));
    }

    private static WowzaDiscoveryProvider Discovery(HttpClient client) => new(client,
        new WowzaServerConfiguration("Synthetic", "TEST", new Uri("http://fixture.invalid/"), "synthetic", true,
            "fixture.invalid", 554, ["test"], ["_default_"], "rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}",
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)),
        new StaticCredentialResolver(new("synthetic-user", "synthetic-password")));

    internal static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BroadcastRouter.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    internal static async Task Eventually(Func<bool> condition, TimeSpan? wait = null)
    {
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
