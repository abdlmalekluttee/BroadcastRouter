using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace BroadcastRouter.Tests;

public sealed class GracefulStopTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooperativeQuitWithinConfiguredBudgetDoesNotForceTermination(bool handoff)
    {
        await using var fixture = new ChildFixture(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "ready" } },
            quitDelayMilliseconds = 1100
        });
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(3));
        var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
        supervisor.LifecycleChanged += events.Enqueue;
        await fixture.Start(supervisor);
        var timer = Stopwatch.StartNew();
        if (handoff) await supervisor.StopForOutputHandoffAsync(fixture.Source, CancellationToken.None);
        else await supervisor.StopAsync(fixture.Source, CancellationToken.None);
        output.WriteLine($"{(handoff ? "handoff" : "normal")} stop elapsed: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.DoesNotContain(events, item => item.State == RouteProcessLifecycleState.ForcedTermination);
        Assert.Equal(0, supervisor.Snapshot().Single().ExitCode);
        Assert.False(supervisor.Snapshot().Single().Running);
        Assert.Single(events, item => item.State == RouteProcessLifecycleState.Exited);
    }

    [Fact]
    public async Task DrainAllowsPendingReadersToReachEofBeforeCancellingThem()
    {
        // Real drain algorithm, with controllable pending readers. A regression
        // that cancels on entry deterministically loses their buffered final data.
        var managedType = typeof(FfmpegProcessSupervisor).GetNestedType("ManagedProcess", BindingFlags.NonPublic)!;
        using var process = new Process();
        var managed = Activator.CreateInstance(managedType, new SourceIdentity("TEST", "fixture", "_default_", "drain"),
            RouteProcessPurpose.Live, process, DateTimeOffset.UnixEpoch)!;
        var cancellation = (CancellationTokenSource)managedType.GetProperty("PumpCancellation")!.GetValue(managed)!;
        var eof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        managedType.GetProperty("ErrorTask")!.SetValue(managed, eof.Task);
        var method = typeof(FfmpegProcessSupervisor).GetMethod("DrainPumpsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var drain = (Task)method.Invoke(null, [managed])!;
        try
        {
            Assert.False(cancellation.IsCancellationRequested, "Readers cancelled before EOF can discard final stderr/progress");
            Assert.False(drain.IsCompleted);
        }
        finally { eof.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(6)); cancellation.Dispose(); }
    }

    [Fact]
    public async Task QuitTailIsRetainedInSessionTranscript()
    {
        var lines = Enumerable.Range(0, 2000).Select(index => $"quit-evidence-{index:D4}").ToArray();
        await using var fixture = new ChildFixture(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "ready" } },
            quitStderr = lines
        });
        await using var telemetry = new SupervisionTelemetry(Path.Combine(fixture.Root, "logs"));
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(3), telemetry: telemetry);
        await fixture.Start(supervisor);
        var session = supervisor.Snapshot().Single();
        await supervisor.StopAsync(fixture.Source, CancellationToken.None);
        await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var transcript = string.Join("\n", Directory.GetFiles(Path.Combine(fixture.Root, "logs"), session.StderrLog!).Select(File.ReadAllText));
        foreach (var line in lines) Assert.Contains(line, transcript);
        Assert.True(telemetry.Status.RetentionComplete);
    }

    internal sealed class ChildFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "router-stop-" + Guid.NewGuid().ToString("N"));
        public SourceIdentity Source { get; } = new("TEST", "fixture", "_default_", "synthetic-stop");
        private readonly string _script;
        public ChildFixture(object script)
        {
            Directory.CreateDirectory(Root);
            _script = Path.Combine(Root, "child.json");
            File.WriteAllText(_script, JsonSerializer.Serialize(script));
        }
        public async Task Start(FfmpegProcessSupervisor supervisor)
        {
            var executable = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(_script);
            await supervisor.StartOwnedProcessForTestingAsync(Source, start);
            await FaultHarnessTests.Eventually(() => supervisor.Snapshot().Single().RecentErrors.Contains("ready"));
        }
        public ValueTask DisposeAsync() { Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
