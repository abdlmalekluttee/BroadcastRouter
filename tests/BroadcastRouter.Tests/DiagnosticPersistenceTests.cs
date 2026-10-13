using System.Reflection;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticPersistenceTests
{
    [Fact]
    public async Task ServiceLifecycleDoesNotWaitForLockedDiagnosticWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-service-diagnostic-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"));
        await store.InitializeAsync();
        var reporter = new ServiceLifecycleReporter(store, NullLogger<ServiceLifecycleReporter>.Instance);
        var gate = (SemaphoreSlim)typeof(SqliteDataStore).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        Task? start = null;
        try
        {
            start = reporter.StartAsync(CancellationToken.None);
            Assert.True(await Task.WhenAny(start, Task.Delay(100)) == start,
                "Optional service lifecycle diagnostics blocked host startup.");
            await start;
        }
        finally
        {
            gate.Release(); if (start is not null) await start;
            await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BoundedLongDiagnosticIsRetainedWithoutUsingMessageAsOversizedKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-long-diagnostic-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"));
        try
        {
            await store.InitializeAsync();
            var message = new string('x', 4096);
            Assert.True(store.EnqueueDiagnostic("Warning", "Synthetic", message));
            await store.FlushDiagnosticsAsync();
            Assert.Equal(message, Assert.Single(await store.ReadLogsAsync()).Message);
        }
        finally
        {
            await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelledEssentialWriteCannotBeAcknowledgedOrReplacedByQueueAdmission()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-essential-write-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"));
        await store.InitializeAsync();
        var gate = (SemaphoreSlim)typeof(SqliteDataStore).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var route = new BroadcastRouter.Domain.RuntimeRoute("SYNTHETIC/source", "Synthetic", "OUTPUT-A", "Output A", "1080i50",
                BroadcastRouter.Domain.RouteState.Reserved, BroadcastRouter.Domain.AssignmentMode.Manual, false,
                0, 0, null, null, null, 0, 0, null, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, null);
            var save = store.SaveRouteAsync(route, cancellationToken: cancellation.Token);
            Assert.False(save.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            Assert.Empty(await store.LoadRoutesAsync());
        }
        finally
        {
            gate.Release(); await store.StopDiagnosticsAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CoordinatorDiagnosticCallbackDoesNotWaitForDatabaseWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-diagnostic-lock-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteDataStore(Path.Combine(root, "router.db"));
        await store.InitializeAsync();
        using var coordinator = new RouterCoordinator(store, null!, null!, NullLogger<RouterCoordinator>.Instance);
        var gate = (SemaphoreSlim)typeof(SqliteDataStore).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        Task? callback = null;
        try
        {
            callback = (Task)typeof(RouterCoordinator).GetMethod("LogAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(coordinator, ["Warning", "Synthetic", "diagnostic fixture", null, null, CancellationToken.None])!;
            Assert.True(await Task.WhenAny(callback, Task.Delay(250)) == callback,
                "A diagnostic callback must not wait behind the database writer.");
            await callback;
        }
        finally
        {
            gate.Release();
            if (callback is not null) await callback.WaitAsync(TimeSpan.FromSeconds(5));
            // Reading drains admitted diagnostics before observing committed rows.
            Assert.Contains(await store.ReadLogsAsync(), entry => entry.Message.Contains("diagnostic fixture"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
