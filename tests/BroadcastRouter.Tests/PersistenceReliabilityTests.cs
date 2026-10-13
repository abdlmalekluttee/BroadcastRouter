using System.Text.Json;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class PersistenceReliabilityTests
{
    // Repeated capacity evictions must not perform a volume durability barrier
    // per lost segment. The first loss must nevertheless survive a crash.
    [Fact]
    public async Task SlowDurableLossMarkersDoNotStarveFiveSecondFlushBarrier()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-slow-marker-" + Guid.NewGuid().ToString("N"));
        Task? flush = null;
        var telemetry = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 3,
            readVolumeSpace: TelemetryTestVolume.Read, flushDurable: stream =>
            {
                Thread.Sleep(450); // deterministic slow storage, not a longer application timeout
                stream.Flush(flushToDisk: false);
            });
        try
        {
            await telemetry.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 100; index++)
                telemetry.Stderr(Guid.NewGuid(), new string('x', 2000));
            flush = telemetry.FlushAsync();
            var completed = await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5))) == flush;
            Assert.True(completed, "Repeated loss-marker durability barriers starved the original five-second flush deadline.");
            await flush;
            Assert.True(telemetry.Status.CapacityEvictions > 5);
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "retention-loss.json")));
            Assert.Equal(telemetry.Status.CapacityEvictions, marker.RootElement.GetProperty("CapacityEvictions").GetInt64());
        }
        finally
        {
            if (flush is not null) await flush.WaitAsync(TimeSpan.FromSeconds(60));
            await telemetry.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CrashBeforeCounterCheckpointCannotReportCompleteHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-marker-crash-" + Guid.NewGuid().ToString("N"));
        var snapshot = root + "-crash-copy";
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SupervisionTelemetry? telemetry = null;
        telemetry = new SupervisionTelemetry(root, maxFileBytes: 16384, maxFiles: 3,
            readVolumeSpace: TelemetryTestVolume.Read)
        {
            BeforeWriteForTesting = async () =>
            {
                if (telemetry!.Status.CapacityEvictions < 2) return;
                paused.TrySetResult();
                await release.Task;
            }
        };
        try
        {
            for (var index = 0; index < 100; index++) telemetry.Stderr(Guid.NewGuid(), new string('x', 2000));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.CreateDirectory(snapshot);
            File.Copy(Path.Combine(root, "retention-loss.json"), Path.Combine(snapshot, "retention-loss.json"));
            await using var restarted = new SupervisionTelemetry(snapshot, readVolumeSpace: TelemetryTestVolume.Read);
            await restarted.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(restarted.Status.RetentionComplete);
            Assert.True(restarted.Status.CapacityEvictions > 0);
        }
        finally
        {
            release.TrySetResult();
            await telemetry.DisposeAsync();
            Directory.Delete(root, true);
            if (Directory.Exists(snapshot)) Directory.Delete(snapshot, true);
        }
    }
}
