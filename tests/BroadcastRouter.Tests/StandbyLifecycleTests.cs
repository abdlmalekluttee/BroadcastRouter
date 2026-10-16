using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StandbyLifecycleTests
{
    [Fact]
    public async Task SlowSlateRenderingDoesNotBlockSettingsOrOtherPortReconciliation()
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var original = lab.Supervisor.Snapshot().Single(p => p.Running);
        var ports = lab.Get<Dictionary<string, DeckLinkPort>>("_ports");
        ports["SECOND-OUTPUT"] = ports["TEST-OUTPUT"] with { StableId = "SECOND-OUTPUT", FfmpegName = "Second synthetic output" };
        lab.Settings.DeckLinkPortOverrides.Add(new() { StableId = "SECOND-OUTPUT", IsOutputPort = true,
            StandbyEnabled = true, StandbyPresetId = lab.Settings.DeckLinkPortOverrides[0].StandbyPresetId });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(FfmpegProcessSupervisor).GetField("_standbySlates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(lab.Supervisor, new StandbySlateCache(Path.Combine(lab.Root, "slates"), async (_, token) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(token); throw new IOException("Synthetic delayed render"); }));
        var save = lab.Coordinator.SaveSettingsAsync(lab.Coordinator.GetSettings());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var first = await save.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(first.Settings.ConfigurationRevision, lab.Coordinator.SettingsRevision);
            await lab.Coordinator.SaveSettingsAsync(lab.Coordinator.GetSettings()).WaitAsync(TimeSpan.FromSeconds(2));
            await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
            var statuses = lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys");
            Assert.Contains("SECOND-OUTPUT", statuses.Keys); // First renderer cannot starve this port's status.
            Assert.Contains("prepar", statuses["SECOND-OUTPUT"].Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally { release.TrySetResult(); await save; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedHealthyOwnerCannotForgiveFailedReplacement(bool qualifiedBeforeFailure)
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var running = typeof(FfmpegProcessSupervisor).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lab.Supervisor)!;
        var managed = ((System.Collections.IEnumerable)running.GetType().GetProperty("Values")!.GetValue(running)!).Cast<object>().Single();
        var frames = 0;
        void Advance(int seconds = 1)
        {
            lab.Time.Advance(TimeSpan.FromSeconds(seconds)); frames += seconds * 25;
            managed.GetType().GetProperty("Progress")!.SetValue(managed,
                new FfmpegProgressSnapshot(frames, 25, TimeSpan.FromSeconds(frames / 25.0), 0, 0, 1, lab.Clock.Now, false));
        }
        for (var i = 0; i < (qualifiedBeforeFailure ? 62 : 1); i++)
        { Advance(); await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None); }
        var budget = lab.Get<System.Collections.Concurrent.ConcurrentDictionary<string, StandbyRetryBudget>>("_standbyBudgets")["TEST-OUTPUT"];
        var attempts = 0;
        typeof(FfmpegProcessSupervisor).GetField("_standbySlates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(lab.Supervisor, new StandbySlateCache(Path.Combine(lab.Root, "slates"), (_, _) =>
            { attempts++; throw new IOException("Replacement remains broken"); }));
        lab.Settings.DeckLinkPortOverrides.Single().StandbyLabel = "Broken replacement";
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        await AwaitPreparation(lab);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var due = budget.NextAttempt;
        // Old output advances immediately, before this replacement's retry deadline.
        managed.GetType().GetProperty("Progress")!.SetValue(managed,
            new FfmpegProgressSnapshot(++frames, 25, TimeSpan.FromSeconds(frames / 25.0), 0, 0, 1, lab.Clock.Now, false));
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.Equal(due, budget.NextAttempt);
        Assert.Equal(1, attempts);
        for (var attempt = 1; attempt < 3; attempt++)
        {
            Advance(4); await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
            await AwaitPreparation(lab);
            await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        }
        Assert.True(budget.CircuitOpen);
        due = budget.NextAttempt;
        for (var i = 0; i < 62; i++)
        { Advance(); await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None); }
        Assert.True(budget.CircuitOpen);
        Assert.Equal(due, budget.NextAttempt);
        Assert.Equal(3, attempts);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryStandbyPreparesChangedSlateBeforeReplacingItsOwner(bool renderFails)
    {
        await using var lab = await Create(true, exits: false);
        await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        var original = lab.Supervisor.Snapshot().Single(p => p.Running);
        var preset = lab.Settings.Presets.Single(p => p.Id == lab.Route.PresetId);
        preset.Width = 2; preset.Height = 2;
        var attempts = 0;
        typeof(FfmpegProcessSupervisor).GetField("_standbySlates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(lab.Supervisor, new StandbySlateCache(Path.Combine(lab.Root, "slates"), async (start, token) =>
            {
                attempts++;
                Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
                if (renderFails) throw new IOException("Synthetic render failure");
                await File.WriteAllBytesAsync(start.ArgumentList[^1], Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAAE0lEQVR4nGP8//8/AwMDCwMYAAAkFAMDuxa40wAAAABJRU5ErkJggg=="), token);
            }));
        lab.Settings.DeckLinkPortOverrides.Single().StandbyLabel = "Changed recovery label";
        await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        await AwaitPreparation(lab);
        await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        Assert.Equal(1, attempts);
        if (renderFails) Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
        else Assert.DoesNotContain(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
        for (var i = 0; i < 3; i++) await lab.Invoke("StartFallbackAsync", lab.Route, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Equal(renderFails ? 1 : 2, lab.Events.Count(e => e.State == RouteProcessLifecycleState.Started));
    }
    [Fact]
    public async Task FailedSlatePreparationKeepsExactPreviousStandbyOwner()
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var original = lab.Supervisor.Snapshot().Single(p => p.Running);
        var field = typeof(FfmpegProcessSupervisor).GetField("_standbySlates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var attempts = 0;
        field.SetValue(lab.Supervisor, new StandbySlateCache(Path.Combine(lab.Root, "slates"),
            (_, _) => { attempts++; throw new IOException("Synthetic slate preparation failure"); }));
        lab.Settings.DeckLinkPortOverrides.Single(p => p.StableId == "TEST-OUTPUT").StandbyLabel = "New label";
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        await AwaitPreparation(lab);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
        Assert.Single(lab.Events, e => e.State == RouteProcessLifecycleState.Started);
        Assert.Contains("retained", lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"].Summary);
        for (var i = 0; i < 5; i++) await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.Equal(1, attempts); // Existing healthy output must not create a render retry storm.
        var retainedStatus = lab.Get<Dictionary<string, PortStandbyStatus>>("_standbys")["TEST-OUTPUT"];
        Assert.Equal(PortStandbyState.Failed, retainedStatus.State);
        Assert.Contains("retained", retainedStatus.Summary);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("validation")]
    public async Task ChangesDuringPreparationCannotStopPreviousOwner(string change)
    {
        await using var lab = await Create(false, exits: false);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        var original = lab.Supervisor.Snapshot().Single(p => p.Running);
        var preset = lab.Settings.Presets.Single(p => p.Id == lab.Settings.DeckLinkPortOverrides.Single().StandbyPresetId);
        preset.Width = 2; preset.Height = 2;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = typeof(FfmpegProcessSupervisor).GetField("_standbySlates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(lab.Supervisor, new StandbySlateCache(Path.Combine(lab.Root, "slates"), async (start, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            await File.WriteAllBytesAsync(start.ArgumentList[^1], Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAAE0lEQVR4nGP8//8/AwMDCwMYAAAkFAMDuxa40wAAAABJRU5ErkJggg=="), token);
        }));
        var pending = lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
        if (change == "label") lab.Settings.DeckLinkPortOverrides.Single().StandbyLabel = "Newer label";
        else lab.Set("_validation", new MediaToolValidation(ToolValidationState.Invalid, "fake", "fake", false, false, 0, [], lab.Clock.Now));
        release.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        await AwaitPreparation(lab);
        await lab.Invoke("ReconcilePortStandbysAsync", CancellationToken.None);
        Assert.Contains(lab.Supervisor.Snapshot(), p => p.SessionId == original.SessionId && p.Running);
    }

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
            stop = lab.Invoke("StopRouteAsync", lab.Source.Value, true, CancellationToken.None, true, false);
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
    [InlineData("standby-filters")]
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
            else if (change == "standby-filters") lab.Set("_validation", new MediaToolValidation(ToolValidationState.Valid, "fake", "fake", true, true, 1, [], lab.Clock.Now, PortStandbySupported: false));
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

    private static async Task AwaitPreparation(OfflineFallbackTests.RecoveryLab lab)
    {
        var values = lab.Get<System.Collections.IDictionary>("_standbyPreparations").Values.Cast<object>().ToArray();
        foreach (var value in values)
        {
            var task = (Task)value.GetType().GetProperty("Task")!.GetValue(value)!;
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (IOException) { } // The next coordinator observation must record this actual render failure.
        }
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
