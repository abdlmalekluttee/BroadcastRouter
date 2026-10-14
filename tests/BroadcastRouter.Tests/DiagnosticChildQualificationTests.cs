using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticChildQualificationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealShortLivedChildrenDoNotCreateUnboundedPersistenceWork()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("BROADCASTROUTER_PERSISTENCE_CHILD_COUNT"), out var configured)
            ? configured : 32;
        Assert.InRange(count, 1, 100000);
        var root = Path.Combine(Path.GetTempPath(), "router-persistence-children-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"), readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        await store.InitializeAsync();
        var persistenceFailures = new ConcurrentDictionary<string, int>();
        var diagnosticQueue = (DiagnosticPersistenceQueue)typeof(SqliteDataStore)
            .GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var writerField = typeof(DiagnosticPersistenceQueue).GetField("_write", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actualWriter = (Func<IReadOnlyList<DiagnosticLogRecord>, CancellationToken, Task>)writerField.GetValue(diagnosticQueue)!;
        writerField.SetValue(diagnosticQueue, (Func<IReadOnlyList<DiagnosticLogRecord>, CancellationToken, Task>)(async (records, token) =>
        {
            try { await actualWriter(records, token); }
            catch (Exception exception)
            {
                var key = exception.GetType().Name + ": " + exception.Message;
                persistenceFailures.AddOrUpdate(key, 1, (_, value) => value + 1);
                throw;
            }
        }));
        using var coordinator = new RouterCoordinator(store, null!, null!, NullLogger<RouterCoordinator>.Instance);
        var callback = (Action<RouteProcessLifecycleEvent>)typeof(RouterCoordinator).GetMethod("OnProcessLifecycleChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action<RouteProcessLifecycleEvent>), coordinator);
        await using var supervisor = new FfmpegProcessSupervisor(new("unused"), TimeSpan.FromSeconds(1));
        var exited = new ConcurrentDictionary<string, TaskCompletionSource<int?>>();
        var callbacks = new ConcurrentQueue<double>();
        var actualStarts = 0; var actualExits = 0; var next = 0;
        var routeLatencies = new ConcurrentQueue<double>();
        using var controlStop = new CancellationTokenSource();
        var replaceRoute = typeof(RouterCoordinator).GetMethod("ReplaceRouteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reconcile = typeof(RouterCoordinator).GetMethod("ReconcileRoutesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var control = Task.Run(async () =>
        {
            var version = 0;
            while (!controlStop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var route = new RuntimeRoute("SYNTHETIC/fixture/default/control", "Synthetic control", null, null,
                    "1080i50", RouteState.WaitingForStream, AssignmentMode.Automatic, false,
                    ++version, 0, null, null, null, 0, 0, null, SupervisionClock.System.Now, null, null);
                try { await ((Task)replaceRoute.Invoke(coordinator, [route, null, CancellationToken.None, true])!).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException($"Essential route write stalled: threadPoolThreads={ThreadPool.ThreadCount}, "
                        + $"pendingWork={ThreadPool.PendingWorkItemCount}, diagnostics={store.DiagnosticStatus}", exception);
                }
                await ((Task)reconcile.Invoke(coordinator, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(2));
                var saved = Assert.Single(await store.LoadRoutesAsync());
                Assert.Equal(version, saved.Priority); Assert.Equal(RouteState.WaitingForStream, saved.State);
                routeLatencies.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                try { await Task.Delay(100, controlStop.Token); }
                catch (OperationCanceledException) when (controlStop.IsCancellationRequested) { break; }
            }
        });
        supervisor.LifecycleChanged += value =>
        {
            var start = Stopwatch.GetTimestamp(); callback(value);
            callbacks.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (value.State == RouteProcessLifecycleState.Started) Interlocked.Increment(ref actualStarts);
            if (value.State == RouteProcessLifecycleState.Exited)
            {
                Interlocked.Increment(ref actualExits);
                if (exited.TryGetValue(value.Source.Value, out var signal)) signal.TrySetResult(value.ExitCode);
            }
        };
        var timer = Stopwatch.StartNew();
        try
        {
            var executable = Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg",
                "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async worker =>
            {
                var source = new SourceIdentity("SYNTHETIC", "fixture", "default", "worker-" + worker);
                var script = Path.Combine(root, $"child-{worker}.json");
                var release = Path.Combine(root, $"release-{worker}");
                // Persistence qualification starts after ownership admission.
                // A real 1ms child may otherwise exit before Windows job assignment;
                // helper-admission/crash containment is qualified separately in P2-32.
                await File.WriteAllTextAsync(script, System.Text.Json.JsonSerializer.Serialize(new
                    { events = Array.Empty<object>(), exitAfterMilliseconds = 1, continueFile = release }));
                while (Interlocked.Increment(ref next) <= count)
                {
                    File.Delete(release);
                    var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    exited[source.Value] = completion;
                    var start = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    start.ArgumentList.Add(script);
                    await supervisor.StartOwnedProcessForTestingAsync(source, start);
                    await File.WriteAllTextAsync(release, "admitted");
                    Assert.Equal(0, await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                    await supervisor.StopAsync(source, CancellationToken.None);
                    if (control.IsFaulted) await control;
                }
            }));
            await supervisor.DisposeAsync();
            controlStop.Cancel(); await control;
            await store.FlushDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(count, actualStarts); Assert.Equal(count, actualExits);
            Assert.Empty(supervisor.RunningSnapshot());
            Assert.InRange(store.DiagnosticStatus.PeakPending, 1, 2048);
            Assert.Equal(0, store.DiagnosticStatus.Pending);
            Assert.Equal(0, store.DiagnosticStatus.Dropped);
            Assert.Equal(0, store.DiagnosticStatus.WriteFailures);
            Assert.Equal(0, store.DiagnosticStatus.Abandoned);
            Assert.Equal(callbacks.Count, store.DiagnosticStatus.Written);
            var times = callbacks.Order().ToArray();
            var p99 = times[(int)Math.Floor((times.Length - 1) * .99)];
            Assert.True(p99 < 100, $"Diagnostic callback p99 exceeded100ms: {p99}");
            Assert.True(times[^1] < 1000, $"Diagnostic callback max exceeded1s: {times[^1]}");
            Assert.NotEmpty(routeLatencies);
            Assert.True(routeLatencies.Max() < 2000, "Route persistence/reconciliation exceeded two seconds during child churn.");
            output.WriteLine($"duration={timer.Elapsed}; actualChildren={count}; starts={actualStarts}; exits={actualExits}; "
                + $"callbacks={times.Length}; callbackP99Ms={p99:F3}; callbackMaxMs={times[^1]:F3}; "
                + $"peakPending={store.DiagnosticStatus.PeakPending}; coalesced={store.DiagnosticStatus.Coalesced}; "
                + $"earlyEvictions={store.DiagnosticStatus.RetentionEvictions}; "
                + $"routeTurns={routeLatencies.Count}; routeMaxMs={routeLatencies.Max():F3}; "
                + $"transactionMaxMs={store.DiagnosticStatus.MaxTransaction.TotalMilliseconds:F3}");
        }
        finally
        {
            output.WriteLine($"finalElapsed={timer.Elapsed}; actualStarts={actualStarts}; actualExits={actualExits}; "
                + $"routeTurns={routeLatencies.Count}; routeMaxMs={(routeLatencies.IsEmpty ? 0 : routeLatencies.Max()):F3}; "
                + $"diagnostics={store.DiagnosticStatus}; controlStatus={control.Status}");
            foreach (var failure in persistenceFailures) output.WriteLine($"writeFailure={failure.Key}; batches={failure.Value}");
            controlStop.Cancel();
            try { await control; }
            finally
            {
            await supervisor.DisposeAsync(); await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
            }
        }
    }
}
