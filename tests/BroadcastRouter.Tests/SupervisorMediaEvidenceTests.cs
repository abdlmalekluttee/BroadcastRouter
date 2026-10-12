using System.IO.Pipes;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class SupervisorMediaEvidenceTests
{
    [Fact]
    public async Task LiveOwnerReportsIndependentEvidenceAndClosesItsPipesOnStop()
    {
        await using var fixture = new MediaChildFixture();
        var time = new ManualTimeProvider();
        var clock = new SupervisionClock(time);
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable, UseIndependentMediaStats:true), TimeSpan.FromSeconds(2), clock);
        await fixture.Start(supervisor);
        var first = Assert.Single(supervisor.RunningSnapshot());
        Assert.NotNull(first.IndependentMedia);
        Assert.Equal(4, first.IndependentMedia.Signals.Count);
        for (var step = 0; step <= 4; step++)
        {
            if (step > 0) time.Advance(TimeSpan.FromMilliseconds(500));
            await FaultHarnessTests.Eventually(() => supervisor.TryGetRunning(fixture.Source.Identity, out var snapshot)
                && snapshot.IndependentMedia!.Signals.All(signal => signal.LastAdvancedAt == clock.Now));
        }
        Assert.All(Assert.Single(supervisor.RunningSnapshot()).IndependentMedia!.Signals,
            signal => Assert.Equal(MediaSignalState.Healthy, signal.State));
        Assert.True(supervisor.TryGetRunning(fixture.Source.Identity, out var running));
        Assert.NotNull(running.IndependentMedia);
        await supervisor.StopAsync(fixture.Source.Identity, CancellationToken.None);
        var final = Assert.Single(supervisor.Snapshot());
        Assert.False(final.Running);
        Assert.Equal(0, final.ExitCode);
        Assert.True(final.IndependentMedia!.MayRecover); // exited, not a live health inference
        Assert.All(final.IndependentMedia.Signals, signal => Assert.Equal(MediaSignalState.Unknown, signal.State));
        await AssertPipesClosed(fixture);
    }

    [Fact]
    public async Task EarlyProcessExitDisposesEvenUnconnectedObservationPipes()
    {
        await using var fixture = new MediaChildFixture(exitImmediately:true);
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable, UseIndependentMediaStats:true), TimeSpan.FromSeconds(2));
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.LifecycleChanged += value => { if (value.State == RouteProcessLifecycleState.Exited) exited.TrySetResult(); };
        await fixture.Start(supervisor);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var final = Assert.Single(supervisor.Snapshot());
        Assert.NotNull(final.IndependentMedia);
        Assert.True(final.IndependentMedia.MayRecover);
        await AssertPipesClosed(fixture);
    }

    [Fact]
    public async Task UnqualifiedToolHasNoStatsArgumentsOrInventedEvidence()
    {
        await using var fixture = new MediaChildFixture();
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromSeconds(2));
        await fixture.Start(supervisor);
        Assert.Null(Assert.Single(supervisor.RunningSnapshot()).IndependentMedia);
        await FaultHarnessTests.Eventually(() => File.Exists(Path.Combine(fixture.Root, "arguments.json")));
        await supervisor.StopAsync(fixture.Source.Identity, CancellationToken.None);
        var arguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "arguments.json")))!;
        Assert.DoesNotContain(arguments, arg => arg.StartsWith("-stats_", StringComparison.Ordinal));
    }

    private static async Task AssertPipesClosed(MediaChildFixture fixture)
    {
        var arguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "arguments.json")))!;
        var pipes = arguments.Where(arg => arg.StartsWith(@"\\.\pipe\broadcast-router-media-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, pipes.Length);
        foreach (var endpoint in pipes)
        {
            using var client = new NamedPipeClientStream(".", endpoint[9..], PipeDirection.Out, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(deadline.Token));
        }
    }

    private sealed class MediaChildFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "router-recovery-media-" + Guid.NewGuid().ToString("N"));
        internal string Executable => Path.Combine(Root, "BroadcastRouter.FakeFfmpeg.exe");
        internal DiscoveredSource Source { get; } = new(new("TEST", "fixture", "_default_", "synthetic-media"), "Synthetic",
            new Uri("rtsp://example.invalid/fixture"), SourceState.Ready, 0,
            new("h264", "aac", 1920, 1080, 25, null, 48000, 2, true));
        internal MediaChildFixture(bool exitImmediately = false)
        {
            Directory.CreateDirectory(Root);
            var child = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
            foreach (var file in Directory.GetFiles(child, "BroadcastRouter.FakeFfmpeg.*")) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            var events = new List<object>();
            for (var i = 0; i < (exitImmediately ? 0 : 150); i++)
            {
                events.Add(new { atMilliseconds=i*20, channel="-stats_enc_pre:v:0", text=$"brmedia|pre|v|{i}|{i}|1/25|{i}" });
                events.Add(new { atMilliseconds=i*20, channel="-stats_enc_pre:a:0", text=$"brmedia|pre|a|{i}|{i*1024}|1/48000|{i}" });
                events.Add(new { atMilliseconds=i*20, channel="-stats_mux_pre:v:0", text=$"brmedia|mux|v|{i}" });
                events.Add(new { atMilliseconds=i*20, channel="-stats_mux_pre:a:0", text=$"brmedia|mux|a|{i}" });
            }
            File.WriteAllText(Path.Combine(Root, "live.json"), JsonSerializer.Serialize(new { events, captureArguments=true,
                exitAfterMilliseconds=exitImmediately ? (int?)0 : null }));
        }
        internal Task Start(FfmpegProcessSupervisor supervisor) => supervisor.StartAsync(
            new(Source.Identity, "TEST-OUTPUT", "test-mode", RouteState.Starting, AssignmentMode.Manual, false), Source,
            new("TEST-OUTPUT", "TEST-DEVICE", "Synthetic", 0, 0, null, []),
            new("test-mode", "Test mode", new(1920,1080,25,1,"uyvy422"), false, 0), CancellationToken.None);
        public ValueTask DisposeAsync() => new(FixtureDirectory.DeleteAsync(Root));
    }
}
