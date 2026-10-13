using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace BroadcastRouter.Tests;

// CI runs a short exercise. A separately recorded 3600-second local run is
// required by Gate D; the default exercise is not a one-hour qualification.
public sealed class OutputControllerQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentSettingsStartStopAndDisposeWithFaultingChildren()
    {
        var requested = Environment.GetEnvironmentVariable("BROADCASTROUTER_FAKE_QUALIFICATION_SECONDS");
        var seconds = requested is null ? 3 : int.Parse(requested);
        Assert.InRange(seconds, 3, 3600);
        await using var lab = await OfflineFallbackTests.RecoveryLab.Create();
        await lab.InstallFakeMediaTools();
        lab.Settings.DeckLinkPortOverrides.Add(new() { StableId = "TEST-OUTPUT", IsOutputPort = true,
            StandbyPresetId = lab.Settings.Presets[0].Id });
        await using var fixture = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "live.json"), JsonSerializer.Serialize(new {
            events = Array.Empty<object>(), ignoreQuit = true }));
        await using var crashing = new SupervisorMediaEvidenceTests.MediaChildFixture();
        await File.WriteAllTextAsync(Path.Combine(crashing.Root, "live.json"), JsonSerializer.Serialize(new {
            events = Array.Empty<object>(), exitAfterMilliseconds = 30, exitCode = 37 }));
        var original = lab.Owner;
        var timer = Stopwatch.StartNew();
        var rounds = 0;
        var starts = 0;
        var forcedStops = 0;
        var shutdownRaces = 0;
        lab.Supervisor.LifecycleChanged += evt =>
        {
            if (evt.State == RouteProcessLifecycleState.Started) Interlocked.Increment(ref starts);
            if (evt.State == RouteProcessLifecycleState.ForcedTermination) Interlocked.Increment(ref forcedStops);
        };
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var round = ++rounds;
            var save = Task.Run(async () =>
            {
                var settings = lab.Coordinator.GetSettings();
                settings.Routing.InputReadTimeoutMilliseconds = 1000 + round % 10 * 100;
                await lab.Coordinator.SaveSettingsAsync(settings, "Synthetic qualification");
                typeof(RouterCoordinator).GetMethod("EnsureSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(lab.Coordinator, [settings.MediaTools.FfmpegPath, false]);
                Assert.Same(lab.Supervisor, lab.Get<FfmpegProcessSupervisor>("_supervisor"));
            });
            var route = Task.Run(async () =>
            {
                // A faulting/hung worker uses another physical controller in the
                // same supervisor as the continuously running control output.
                var child = round % 2 == 0 ? crashing : fixture;
                var start = StartInfo(child.Executable);
                await lab.Supervisor.StartOwnedProcessForTestingAsync(child.Source.Identity, start);
                if (round % 2 == 0) await Task.Delay(60);
                using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try { await lab.Supervisor.StopAsync(child.Source.Identity, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                await FaultHarnessTests.Eventually(() => !lab.Supervisor.TryGetRunning(child.Source.Identity, out _));
            });
            var shutdown = Task.Run(async () =>
            {
                var supervisor = new FfmpegProcessSupervisor(new(crashing.Executable), TimeSpan.FromMilliseconds(100));
                var events = new ConcurrentQueue<RouteProcessLifecycleEvent>();
                supervisor.LifecycleChanged += events.Enqueue;
                var attempts = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
                {
                    try
                    {
                        await supervisor.StartOwnedProcessForTestingAsync(
                            new("TEST", "race", "default", index.ToString()), StartInfo(crashing.Executable));
                    }
                    catch (ObjectDisposedException ex)
                    { Assert.Equal(typeof(FfmpegProcessSupervisor).FullName, ex.ObjectName); }
                })).ToArray();
                await Task.Yield();
                await supervisor.DisposeAsync();
                await Task.WhenAll(attempts);
                await supervisor.DisposeAsync();
                Assert.Empty(supervisor.RunningSnapshot());
                Assert.All(events.Where(evt => evt.State == RouteProcessLifecycleState.Started), evt =>
                    Assert.Contains(events, final => final.ProcessId == evt.ProcessId && final.State == RouteProcessLifecycleState.Exited));
                Interlocked.Increment(ref shutdownRaces);
            });
            await Task.WhenAll(save, route, shutdown).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(lab.Supervisor.TryGetRunning(lab.Source, out var control));
            Assert.Equal(original.ProcessId, control.ProcessId);
            Assert.Equal(original.SessionId, control.SessionId);
            Assert.DoesNotContain(lab.Events, evt => evt.Source == lab.Source && evt.State == RouteProcessLifecycleState.StopRequested);
            Assert.Equal(lab.Supervisor.RunningSnapshot().Count,
                lab.Supervisor.RunningSnapshot().Select(owner => owner.SessionId).Distinct().Count());
        }
        Assert.True(rounds > 0 && starts > 0 && shutdownRaces > 0);
        output.WriteLine($"Duration={timer.Elapsed}; requested={seconds}s; rounds={rounds}; workerStarts={starts}; forcedStops={forcedStops}; shutdownRaces={shutdownRaces}; controlSessionUnchanged=true");
    }

    private static ProcessStartInfo StartInfo(string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true };
        start.ArgumentList.Add("-progress");
        return start;
    }
}
