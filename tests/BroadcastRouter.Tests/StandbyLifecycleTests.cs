using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StandbyLifecycleTests
{
    [Fact]
    public async Task RepeatedCoordinatorObservationsQualifyOneStableMinute()
    {
        await using var lab = await Create(true, exits: false);
        var budget = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, StandbyRetryBudget>>("_standbyBudgets")
            .GetOrAdd("TEST-OUTPUT", _ => new());
        for (var i = 0; i < 3; i++) budget.RecordFailure(Guid.NewGuid(), lab.Clock.Now, "synthetic circuit failure", 0);
        lab.Time.Advance(TimeSpan.FromMinutes(6));
        await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        var running = typeof(FfmpegProcessSupervisor).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lab.Supervisor)!;
        var managed = ((System.Collections.IEnumerable)running.GetType().GetProperty("Values")!.GetValue(running)!).Cast<object>().Single();
        for (var second = 0; second <= 60; second++)
        {
            managed.GetType().GetProperty("Progress")!.SetValue(managed,
                new FfmpegProgressSnapshot(second + 1, 25, TimeSpan.FromSeconds(second + 1), 0, 0, 1, lab.Clock.Now, false));
            await lab.Invoke("MonitorProcessesAsync", CancellationToken.None);
            await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
            if (second < 60) Assert.True(budget.CircuitOpen);
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.False(budget.CircuitOpen);
        Assert.Equal(PortStandbyState.Running, lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"].State);
    }

    [Fact]
    public async Task PendingFallbackCannotReopenStoppedSavedRouteOrCreateSecondOwner()
    {
        await using var lab = await Create(true, exits: false);
        var staleRoute = lab.Route;
        var portGate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>("_portGates")
            .GetOrAdd("TEST-OUTPUT", _ => new(1, 1));
        await portGate.WaitAsync();
        Task stop, retry;
        try
        {
            stop = lab.Invoke("StopRouteAsync", lab.Source.Value, true, CancellationToken.None, true);
            retry = lab.Invoke("StartFallbackAsync", staleRoute, CancellationToken.None);
            Assert.False(stop.IsCompleted);
            Assert.False(retry.IsCompleted);
        }
        finally { portGate.Release(); }
        await Task.WhenAll(stop, retry).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RouteState.WaitingForStream, lab.Route.State);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var owners = lab.Supervisor.Snapshot().Where(p => p.Running).ToArray();
        Assert.Single(owners);
        Assert.Equal(RouteProcessPurpose.PortStandby, owners[0].Purpose);
    }

    [Theory]
    [InlineData("simulation")]
    [InlineData("validation")]
    public async Task PendingFallbackRechecksExecutionEligibilityAfterPortGate(string change)
    {
        await using var lab = await Create(true, exits: false);
        var portGate = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>>("_portGates")
            .GetOrAdd("TEST-OUTPUT", _ => new(1, 1));
        await portGate.WaitAsync();
        Task retry;
        try
        {
            retry = lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
            Assert.False(retry.IsCompleted);
            if (change == "simulation") lab.Settings.SimulationMode = true;
            else lab.Set("_validation", new MediaToolValidation(ToolValidationState.Invalid, "fake", "fake", false, false, 0, [], lab.Clock.Now));
        }
        finally { portGate.Release(); }
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(lab.Supervisor.Snapshot(), p => p.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledFallbackDoesNotClaimGenerationBeforeFirstFrame(bool operatorRequested)
    {
        await using var lab = await Create(true, exits: false);
        if (operatorRequested) await lab.Coordinator.CommandAsync("standby", lab.Source.Value);
        else await lab.Invoke("ScheduleRetryAsync", lab.Route, "SyntheticInputFailure", "synthetic fault", CancellationToken.None);
        Assert.Equal(RouteState.Reconnecting, lab.Route.State);
        Assert.True(lab.Supervisor.IsRunning(lab.Source, RouteProcessPurpose.Fallback));
    }

    [Fact]
    public async Task HalfOpenCircuitKeepsVisibleAlarmAfterFirstFrame()
    {
        await using var lab = await Create(false, exits: false);
        await File.WriteAllTextAsync(Path.Combine(lab.Root, "live.json"), JsonSerializer.Serialize(new
        { events = new[] { new { atMilliseconds = 0, channel = "stdout", text = "frame=1\nout_time_us=40000\nprogress=continue" } } }));
        var budget = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, StandbyRetryBudget>>("_standbyBudgets")
            .GetOrAdd("TEST-OUTPUT", _ => new());
        for (var i = 0; i < 3; i++) budget.RecordFailure(Guid.NewGuid(), lab.Clock.Now, "synthetic circuit failure", 0);
        lab.Time.Advance(TimeSpan.FromMinutes(6));
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        await FaultHarnessTests.Eventually(() => lab.Supervisor.Snapshot().Any(p => p.Purpose == RouteProcessPurpose.PortStandby && p.Progress?.Frame > 0));
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.Equal(PortStandbyState.Failed, lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"].State);
        Assert.True(budget.CircuitOpen);
    }

    [Fact]
    public async Task NoFirstFrameTimesOutAndRetiresOnlyExactStandbyOwner()
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var owner = lab.Supervisor.Snapshot().Single(p => p.Running);
        lab.Time.Advance(TimeSpan.FromSeconds(lab.Settings.Routing.FirstProgressTimeoutSeconds + 1));
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.False(lab.Supervisor.Snapshot().Single(p => p.SessionId == owner.SessionId).Running);
        Assert.Equal(1, lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started));
        Assert.Contains("No first standby frame", lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"].ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHundredMillisecondChildFailuresStayBoundedForSimulatedDay(bool fallback)
    {
        await using var lab = await Create(fallback);
        if (fallback) await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        for (var second = 0; second < 86400; second++)
        {
            await Tick(lab);
            Assert.True(lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started) < 300,
                "R01: standby exceeded the 24-hour process-start budget");
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.InRange(lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started), 280, 299);
        Assert.Single(await lab.Store.ReadLogsAsync("StandbyCircuitOpen"));
        Assert.InRange((await lab.Store.ReadLogsAsync(limit: 1000)).Count, 1, 300);
    }

    [Fact]
    public async Task ProcessLaunchAloneNeverClaimsStandbyRunning()
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var status = lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"];
        Assert.Equal(PortStandbyState.Starting, status.State);
        Assert.DoesNotContain("on air", status.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeEarlyFailuresRaiseAlarmAndWaitFiveMinutes(bool fallback)
    {
        await using var lab = await Create(fallback);
        if (fallback) await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        for (var second = 0; second < 60; second++)
        {
            await Tick(lab);
            lab.Time.Advance(TimeSpan.FromSeconds(1));
        }
        var starts = lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started);
        Assert.Equal(3, starts);
        var status = lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"];
        Assert.Equal(PortStandbyState.Failed, status.State);
        Assert.Contains("circuit open", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic early failure", status.ErrorMessage);
        for (var second = 0; second < 240; second++)
        { await Tick(lab); lab.Time.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(starts, lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started));
        Assert.Single(await lab.Store.ReadLogsAsync("StandbyCircuitOpen"));
        Assert.True(lab.Get<Dictionary<string, DeckLinkPort>>("_ports")["TEST-OUTPUT"].IsOutputPort);
    }

    internal static async Task<OfflineFallbackTests.RecoveryLab> Create(bool fallback, bool exits = true)
    {
        var lab = await OfflineFallbackTests.RecoveryLab.Create();
        try
        {
            await lab.Supervisor.StopAsync(lab.Source, CancellationToken.None);
            lab.Events.Clear();
            await lab.InstallFakeMediaTools();
            lab.Set("_nextDeckLinkReferenceStatusCheck", DateTimeOffset.MaxValue);
            lab.Set("_validation", new MediaToolValidation(ToolValidationState.Valid, "fake", "fake", true, true, 1, [], lab.Clock.Now,
                PortStandbySupported: true));
            lab.Settings.DeckLinkPortOverrides.Add(new()
            { StableId = "TEST-OUTPUT", IsOutputPort = true, StandbyEnabled = true, StandbyPresetId = lab.Route.PresetId });
            if (!fallback)
            {
                lab.Routes.Clear();
                lab.Get<PortReservationManager>("_reservations").Release("TEST-OUTPUT", lab.Source, force: true);
            }
            await File.WriteAllTextAsync(Path.Combine(lab.Root, "live.json"), JsonSerializer.Serialize(new
            {
                events = new[] { new { atMilliseconds = 0, channel = "stderr", text = "synthetic early failure" } },
                exitAfterMilliseconds = exits ? 100 : (int?)null, exitCode = -5
            }));
            return lab;
        }
        catch { await lab.DisposeAsync(); throw; }
    }

    internal static async Task Tick(OfflineFallbackTests.RecoveryLab lab)
    {
        await lab.Invoke("MonitorProcessesAsync", CancellationToken.None);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        if (lab.Supervisor.Snapshot().Any(p => p.Running))
            await FaultHarnessTests.Eventually(() => lab.Supervisor.Snapshot().All(p => !p.Running));
    }
}
