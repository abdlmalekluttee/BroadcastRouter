using System.Reflection;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class OutputControllerTests
{
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
