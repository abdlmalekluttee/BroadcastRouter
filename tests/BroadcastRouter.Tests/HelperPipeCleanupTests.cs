using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class HelperPipeCleanupTests
{
    [Fact]
    public async Task PreviewReapDeadlineReleasesResourcesWithoutClaimingConfirmedExit()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        CancellationToken processCancellation = default;
        var cleanup = PreviewCleanup.RunAsync(token =>
        {
            processCancellation = token;
            return pending.Task; // Models a native reap that ignores cancellation.
        }, Task.CompletedTask, () => disposed++);
        try
        {
            Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(6))));
            Assert.Contains("exit", (await cleanup)!, StringComparison.OrdinalIgnoreCase);
            Assert.True(processCancellation.IsCancellationRequested);
            Assert.Equal(1, disposed);
        }
        finally
        {
            pending.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrFaultedPreviewReaderCannotHoldStopGateOrReportCleanStop(bool faulted)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "live.json"), JsonSerializer.Serialize(new
            { events = Array.Empty<object>(), ignoreQuit = true }));
        await using var preview = new BrowserPreviewSupervisor();
        await preview.StartAsync(new(new("TEST", "fixture", "default", "pending-reader"), "Synthetic",
            new Uri("rtsp://localhost/fixture/synthetic"), SourceState.Ready, 0,
            new("h264", null, 1920, 1080, 25, null, null, null, true)),
            new() { FfmpegPath = fixture.Executable });
        var session = typeof(BrowserPreviewSupervisor).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
        var reader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.GetType().GetProperty("ProducerErrors")!.SetValue(session, reader.Task);
        using var exactOwner = Process.GetProcessById(preview.Snapshot.ProducerProcessId!.Value);
        _ = exactOwner.SafeHandle;
        if (faulted) reader.SetException(new IOException("synthetic reader failure"));
        var stop = preview.StopAsync();
        try
        {
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(4))));
            await stop;
            Assert.Equal(PreviewState.Failed, preview.Snapshot.State);
            Assert.Contains("drain", preview.Snapshot.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.True(exactOwner.HasExited);
            await preview.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            reader.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("auxiliary", true)]
    [InlineData("probe", true)]
    [InlineData("preview", true)]
    [InlineData("auxiliary", false)]
    [InlineData("probe", false)]
    [InlineData("preview", false)]
    public async Task EndedHelperDoesNotLeaveItsDescendantOrPipeBehind(string kind, bool parentExits)
    {
        using var fixture = new Fixture();
        var script = JsonSerializer.Serialize(new { events = Array.Empty<object>(),
            spawnChildScript = fixture.ChildScript, continueFile = fixture.Release,
            exitAfterMilliseconds = 0, exitCode = 0 });
        foreach (var name in new[] { "live.json", "probe.json", "auxiliary.json" })
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, name), script);
        await using var preview = new BrowserPreviewSupervisor();
        using var stop = new CancellationTokenSource();
        Task operation;
        if (kind == "auxiliary")
            operation = ExternalCommandRunner.RunAsync(fixture.Executable,
                [Path.Combine(fixture.Root, "auxiliary.json")], TimeSpan.FromSeconds(30), stop.Token);
        else if (kind == "probe")
            operation = new FfprobeStreamProbe(fixture.Executable, TimeSpan.FromSeconds(30))
                .ProbeAsync(new Uri("rtsp://localhost/fixture/synthetic"), stop.Token);
        else
        {
            await preview.StartAsync(new(new("TEST", "fixture", "default", "synthetic"), "Synthetic",
                new Uri("rtsp://localhost/fixture/synthetic"), SourceState.Ready, 0,
                new("h264", null, 1920, 1080, 25, null, null, null, true)),
                new() { FfmpegPath = fixture.Executable }, stop.Token);
            operation = Task.CompletedTask;
        }
        try
        {
            await fixture.RetainChildAsync();
            if (!parentExits)
            {
                if (kind == "preview") await preview.StopAsync().WaitAsync(TimeSpan.FromSeconds(7));
                else
                {
                    stop.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(7)));
                }
            }
            else if (kind == "preview")
            {
                await File.WriteAllTextAsync(fixture.Release, "release");
                await FaultHarnessTests.Eventually(() => preview.Snapshot.State != PreviewState.Running);
                await preview.StopAsync().WaitAsync(TimeSpan.FromSeconds(7));
            }
            else
            {
                await File.WriteAllTextAsync(fixture.Release, "release");
                await operation.WaitAsync(TimeSpan.FromSeconds(7));
            }
            await Task.Delay(100);
            Assert.True(fixture.Child!.HasExited, "Owned descendant survived completed " + kind + " helper.");
        }
        finally
        {
            stop.Cancel();
            fixture.ReapChild();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(7)); } catch { }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "router-pipe-cleanup-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Root, "BroadcastRouter.FakeFfmpeg.exe");
        public string ChildScript => Path.Combine(Root, "descendant.json");
        public string Release => Path.Combine(Root, "release");
        public Process? Child { get; private set; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var bin = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg", "bin", "Release", "net8.0");
            foreach (var file in Directory.GetFiles(bin)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            File.WriteAllText(ChildScript, JsonSerializer.Serialize(new { events = Array.Empty<object>(),
                startMarker = Path.Combine(Root, "descendant.started"), ignoreQuit = true }));
        }
        public async Task RetainChildAsync()
        {
            var marker = Path.Combine(Root, "descendant.started");
            await FaultHarnessTests.Eventually(() => File.Exists(marker));
            using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            Child = Process.GetProcessById(identity.RootElement.GetProperty("id").GetInt32());
            _ = Child.SafeHandle;
            Assert.Equal(identity.RootElement.GetProperty("startedUtc").GetDateTime(), Child.StartTime.ToUniversalTime());
        }
        public void ReapChild()
        {
            if (Child is null) return;
            if (!Child.HasExited) Child.Kill();
            Assert.True(Child.WaitForExit(5000));
            Child.Dispose(); Child = null;
        }
        public void Dispose()
        {
            ReapChild();
            var until = Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(Root, true); return; }
                catch (IOException) when (until.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
            }
        }
    }
}
