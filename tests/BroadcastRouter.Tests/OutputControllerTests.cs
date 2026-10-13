using System.Reflection;
using BroadcastRouter.Domain;
using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class OutputControllerTests
{
    [Fact]
    public async Task FutureStartsUsePublishedGenerationWhileExistingSessionKeepsItsGeneration()
    {
        await using var original = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var next = new SupervisorMediaEvidenceTests.MediaChildFixture(identity: new("TEST", "fixture", "default", "next"));
        await using var supervisor = new FfmpegProcessSupervisor(new(original.Executable), TimeSpan.FromSeconds(1));
        await original.Start(supervisor);
        var first = Assert.Single(supervisor.RunningSnapshot());
        supervisor.UpdateConfiguration(new(next.Executable), TimeSpan.FromMilliseconds(100));
        var port = new DeckLinkPort("TEST-NEXT", "TEST-NEXT-DEVICE", "Synthetic", 0, 0, null, []);
        var preset = new OutputPreset("test-mode", "Test mode", new(1920, 1080, 25, 1, "uyvy422"), false, 0);
        await supervisor.StartAsync(new(next.Source.Identity, port.StableId, preset.Id, RouteState.Starting,
            AssignmentMode.Manual, false), next.Source, port, preset, CancellationToken.None);
        await FaultHarnessTests.Eventually(() => File.Exists(Path.Combine(next.Root, "arguments.json")));
        Assert.True(supervisor.TryGetRunning(original.Source.Identity, out var retained));
        Assert.Equal(first.SessionId, retained.SessionId);
        Assert.Equal(1, retained.ConfigurationGeneration);
        Assert.True(supervisor.TryGetRunning(next.Source.Identity, out var launched));
        Assert.Equal(2, launched.ConfigurationGeneration);
        Assert.Equal(2, supervisor.ConfigurationGeneration);
    }

    [Fact]
    public async Task ShutdownClosesOwnedJobEvenWhenAnOutputControllerIsUncooperative()
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        var registry = new PortReservationManager();
        var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromMilliseconds(100), reservations: registry);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Start(supervisor);
        var owner = Assert.Single(supervisor.RunningSnapshot());
        using var process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
        var blocked = registry.ControllerFor("TEST-OUTPUT").ExecuteAsync(async () => { entered.SetResult(); await release.Task; }, CancellationToken.None);
        try
        {
            await entered.Task;
            await supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(process.HasExited, "Shutdown left an owned child alive because its controller was busy");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Start(supervisor));
            Assert.True(registry.ControllerFor("TEST-OUTPUT").Status.OperationActive);
        }
        finally
        {
            release.TrySetResult();
            await blocked;
            // Baseline failure must not leak a fixture child.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public void SupervisorHasNoUnboundedSynchronousProcessReap()
    {
        var source = File.ReadAllText(Path.Combine(FaultHarnessTests.RepositoryRoot(),
            "src", "BroadcastRouter.Infrastructure", "FfmpegProcessSupervisor.cs"));
        Assert.DoesNotContain(".WaitForExit()", source);
    }

    [Fact]
    public async Task IdleRecoveryNeverClearsAnActiveOperationOrAnOwnedSession()
    {
        var controller = new OutputController("TEST-OUTPUT");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var work = controller.ExecuteAsync(async () => { entered.SetResult(); await release.Task; }, cancel.Token);
        try
        {
            await entered.Task;
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.False(controller.TryRecoverIdle());
            Assert.NotNull(controller.Status.Alarm);
            release.SetResult();
            await controller.ExecuteAsync(() => Task.CompletedTask, CancellationToken.None);
            var session = new OutputProcessSession(new("TEST", "fixture", "default", "one"), "Live", Guid.NewGuid(), 1, 1);
            controller.Started(session);
            Assert.False(controller.TryRecoverIdle());
            Assert.Equal(session, controller.Session);
            controller.Exited(Guid.NewGuid());
            Assert.False(controller.TryRecoverIdle());
            controller.Exited(session.SessionId);
            Assert.True(controller.TryRecoverIdle(), "A quiescent controller with proven released ownership must recover its alarm");
            Assert.Null(controller.Status.Alarm);
            Assert.Equal(2, controller.Status.ControllerEpoch);
            Assert.False(controller.TryRecoverIdle());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncooperativeIntentHasBoundedCallerWithoutReleasingItsOutput(bool synchronous)
    {
        var controller = new OutputController("TEST-OUTPUT");
        var unrelated = new OutputController("TEST-OTHER");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var operation = Task.Run(() => controller.ExecuteAsync(() =>
        {
            entered.TrySetResult();
            if (synchronous) release.Task.GetAwaiter().GetResult();
            return release.Task;
        }, cancellation.Token));
        Task? replacement = null;
        var replacementRan = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Task.WhenAny(operation, Task.Delay(500));
            Assert.True(operation.IsCompleted, "An uncooperative output intent blocked its caller after cancellation");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            replacement = controller.ExecuteAsync(() => { replacementRan = true; return Task.CompletedTask; }, CancellationToken.None);
            await unrelated.ExecuteAsync(() => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(replacementRan, "Caller cancellation released an output whose work is still active");
        }
        finally
        {
            release.TrySetResult();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            if (replacement is not null) await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("input-timeout")]
    [InlineData("safe-terminate")]
    public async Task ConfigurationGenerationChangeNeverStopsAnExistingOutput(string change)
    {
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        var original = lab.Owner;
        if (change == "input-timeout") lab.Settings.Routing.InputReadTimeoutMilliseconds += 500;
        var executable = lab.Settings.MediaTools.FfmpegPath + (change == "executable" ? ".next" : "");
        typeof(RouterCoordinator).GetMethod("EnsureSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lab.Coordinator, [executable, change == "safe-terminate"]);
        var current = lab.Get<FfmpegProcessSupervisor>("_supervisor");
        try
        {
            Assert.Same(lab.Supervisor, current);
            Assert.True(current.TryGetRunning(lab.Source, out var owner), "A future-start setting must not stop the existing owner");
            Assert.Equal(original.ProcessId, owner.ProcessId);
            Assert.Equal(original.SessionId, owner.SessionId);
            Assert.DoesNotContain(lab.Events, item => item.State == RouteProcessLifecycleState.StopRequested);
        }
        finally { if (!ReferenceEquals(current, lab.Supervisor)) await current.DisposeAsync(); }
    }

    [Theory]
    [InlineData("live")]
    [InlineData("fallback")]
    [InlineData("standby")]
    [InlineData("recovery-standby")]
    public async Task DifferentSourcesCannotOpenTheSamePhysicalOutputTwice(string secondPurpose)
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await using var supervisor = new FfmpegProcessSupervisor(new(fixture.Executable), TimeSpan.FromSeconds(1));
        await fixture.Start(supervisor);
        var original = Assert.Single(supervisor.RunningSnapshot());
        var source = fixture.Source with { Identity = new("TEST", "fixture", "default", "second") };
        var port = new DeckLinkPort("TEST-OUTPUT", "TEST-DEVICE", "Synthetic", 0, 0, null, []);
        var preset = new OutputPreset("test-mode", "Test mode", new(1920, 1080, 25, 1, "uyvy422"), false, 0);
        var slate = new PortStandbyConfiguration(StandbyPattern.Black, null, "Synthetic", false);
        var error = await Record.ExceptionAsync(() => secondPurpose switch
        {
            "live" => supervisor.StartAsync(new(source.Identity, port.StableId, preset.Id, RouteState.Starting,
                AssignmentMode.Manual, false), source, port, preset, CancellationToken.None),
            "fallback" => supervisor.StartFallbackAsync(source.Identity, port, preset, FallbackMode.Black, null, CancellationToken.None),
            "standby" => supervisor.StartPortStandbyAsync(source.Identity, port, preset, slate, CancellationToken.None),
            _ => supervisor.StartRecoveryStandbyAsync(source.Identity, port, preset, slate, CancellationToken.None)
        });
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(original.SessionId, Assert.Single(supervisor.RunningSnapshot()).SessionId);
    }

    [Fact]
    public async Task DisposedSupervisorRefusesNewStartsBeforeAnyChildIsCreated()
    {
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        // Reaching Process.Start would throw a native missing-executable error.
        // Admission must reject first, before consulting a launch target at all.
        var supervisor = new FfmpegProcessSupervisor(new("missing-synthetic-executable.exe"), TimeSpan.FromSeconds(1));
        await supervisor.DisposeAsync();
        var error = await Record.ExceptionAsync(() => fixture.Start(supervisor));
        Assert.IsType<ObjectDisposedException>(error);
        Assert.Empty(supervisor.RunningSnapshot());
    }
}
