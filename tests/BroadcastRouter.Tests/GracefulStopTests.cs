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
        using var process = new OwnedChildProcess();
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
    public async Task FailedReaderIsRecordedWithoutAbandoningExitedOwnerCleanup()
    {
        var managedType = typeof(FfmpegProcessSupervisor).GetNestedType("ManagedProcess", BindingFlags.NonPublic)!;
        using var process = new OwnedChildProcess();
        var managed = Activator.CreateInstance(managedType, new SourceIdentity("TEST", "fixture", "_default_", "drain"),
            RouteProcessPurpose.Live, process, DateTimeOffset.UnixEpoch)!;
        var cancellation = (CancellationTokenSource)managedType.GetProperty("PumpCancellation")!.GetValue(managed)!;
        try
        {
            managedType.GetProperty("ErrorTask")!.SetValue(managed,
                Task.FromException(new IOException("synthetic pipe failure rtsp://FAKE_USER:FAKE_PASSWORD@192.0.2.6/private")));
            var method = typeof(FfmpegProcessSupervisor).GetMethod("DrainPumpsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var drain = (Task<bool>)method.Invoke(null, [managed])!;
            Assert.False(await drain);
            var errors = (ConcurrentQueue<string>)managedType.GetProperty("Errors")!.GetValue(managed)!;
            Assert.Contains(errors, text => text.Contains("stream reader failed"));
            Assert.DoesNotContain(errors, text => text.Contains("FAKE_PASSWORD") || text.Contains("192.0.2.6"));
        }
        finally { cancellation.Dispose(); }
    }

    [Fact]
    public async Task TimedOutDrainNeverBecomesEofWhenCancelledReadersFinish()
    {
        var managedType = typeof(FfmpegProcessSupervisor).GetNestedType("ManagedProcess", BindingFlags.NonPublic)!;
        using var process = new OwnedChildProcess();
        var managed = Activator.CreateInstance(managedType, new SourceIdentity("TEST", "fixture", "_default_", "drain"),
            RouteProcessPurpose.Live, process, DateTimeOffset.UnixEpoch)!;
        using var cancellation = (CancellationTokenSource)managedType.GetProperty("PumpCancellation")!.GetValue(managed)!;
        var reader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Match the production pumps: cancellation is caught and their Task ends
        // successfully, even though no EOF was received from the pipe.
        using var registration = cancellation.Token.Register(() => reader.TrySetResult());
        managedType.GetProperty("ErrorTask")!.SetValue(managed, reader.Task);
        var method = typeof(FfmpegProcessSupervisor).GetMethod("DrainPumpsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = (Task<bool>)method.Invoke(null, [managed])!;
        var overlapping = (Task<bool>)method.Invoke(null, [managed])!;
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await overlapping.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(await (Task<bool>)method.Invoke(null, [managed])!);
    }

    [Fact]
    public async Task RepeatedCooperativeHandoffsLeaveNoOwnedChildAndDoNotKillAnUnrelatedChild()
    {
        await using var fixture = new ChildFixture(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "ready" } },
            quitDelayMilliseconds = 25
        });
        await using var unrelated = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(3));
        await fixture.Start(unrelated);
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(3));
        var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
        supervisor.LifecycleChanged += events.Enqueue;
        var durations = new List<double>();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await fixture.Start(supervisor);
            var owned = supervisor.Snapshot().Single();
            using var handle = Process.GetProcessById(owned.ProcessId);
            var timer = Stopwatch.StartNew();
            await supervisor.StopForOutputHandoffAsync(fixture.Source, CancellationToken.None);
            durations.Add(timer.Elapsed.TotalMilliseconds);
            Assert.True(handle.HasExited);
            Assert.Empty(supervisor.RunningSnapshot());
            Assert.True(unrelated.Snapshot().Single().Running);
        }
        durations.Sort();
        output.WriteLine($"Synthetic 100-cycle stop: p50={durations[49]:F1}ms p99={durations[98]:F1}ms max={durations[99]:F1}ms");
        Assert.Equal(100, events.Count(item => item.State == RouteProcessLifecycleState.Exited));
        Assert.DoesNotContain(events, item => item.State == RouteProcessLifecycleState.ForcedTermination);
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
        await using var telemetry = new SupervisionTelemetry(Path.Combine(fixture.Root, "logs"), readVolumeSpace: TelemetryTestVolume.Read);
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(3), telemetry: telemetry);
        await fixture.Start(supervisor);
        var session = supervisor.Snapshot().Single();
        await supervisor.StopAsync(fixture.Source, CancellationToken.None);
        await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var transcript = Directory.GetFiles(Path.Combine(fixture.Root, "logs"), session.StderrLog!).SelectMany(File.ReadAllLines)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var retained = transcript.Where(document => document.RootElement.GetProperty("Event").GetProperty("SessionId").GetGuid() == session.SessionId)
                .Select(document => document.RootElement.GetProperty("Event").GetProperty("Text").GetString())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var line in lines) Assert.Contains(line, retained);
        }
        finally { foreach (var document in transcript) document.Dispose(); }
        Assert.True(telemetry.Status.RetentionComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPhasesRecordMeasuredDurationAndOutcome(bool ignoreQuit)
    {
        await using var fixture = new ChildFixture(new
        {
            events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "ready" } },
            quitDelayMilliseconds = 50, ignoreQuit
        });
        await using var telemetry = new SupervisionTelemetry(Path.Combine(fixture.Root, "logs"), readVolumeSpace: TelemetryTestVolume.Read);
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromMilliseconds(300), telemetry: telemetry);
        await fixture.Start(supervisor);
        var session = supervisor.Snapshot().Single();
        await supervisor.StopAsync(fixture.Source, CancellationToken.None);
        await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var documents = Directory.GetFiles(Path.Combine(fixture.Root, "logs"), "*.decisions.*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var phases = documents.Select(document => document.RootElement.GetProperty("Event"))
                .Where(evt => evt.GetProperty("Trigger").GetString() == "OwnedProcessStopPhase").ToArray();
            Assert.Contains(phases, evt => evt.GetProperty("Action").GetString() == "QuitSignal");
            Assert.Contains(phases, evt => evt.GetProperty("Action").GetString() == "ExitWait"
                && evt.GetProperty("Result").GetString() == (ignoreQuit ? "DeadlineExpired" : "Exited"));
            Assert.Contains(phases, evt => evt.GetProperty("Action").GetString() == "PipeDrain"
                && evt.GetProperty("Result").GetString() == "EOF");
            Assert.Equal(ignoreQuit, phases.Any(evt => evt.GetProperty("Action").GetString() == "ForcedReap"));
            Assert.All(phases, evt =>
            {
                Assert.Equal(session.SessionId, evt.GetProperty("SessionId").GetGuid());
                Assert.Equal(session.ProcessId, evt.GetProperty("ProcessId").GetInt32());
                Assert.InRange(evt.GetProperty("DurationMilliseconds").GetDouble(), 0, 10000);
                Assert.True(evt.GetProperty("WindowMilliseconds").GetDouble() > 0);
            });
        }
        finally { foreach (var document in documents) document.Dispose(); }
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
