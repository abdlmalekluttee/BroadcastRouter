using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticQueueTests
{
    [Fact]
    public async Task CoalescingNeverHidesWarningBehindEarlierInformation()
    {
        var entered = Signal(); var release = Signal();
        var written = new ConcurrentQueue<DiagnosticLogRecord>();
        await using var queue = new DiagnosticPersistenceQueue(async (batch, _) =>
        {
            entered.TrySetResult(); await release.Task;
            foreach (var record in batch) written.Enqueue(record);
        }, batchSize: 1);
        try
        {
            queue.TryEnqueue("blocked", "Information", "Fixture", "blocked");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.TryEnqueue("exited", "Information", "Fixture", "normal exit");
            queue.TryEnqueue("exited", "Warning", "Fixture", "failed exit");
            release.TrySetResult(); await queue.FlushAsync();
            Assert.Contains(written, row => row.Level == "Warning" && row.Message == "failed exit" && row.Occurrences == 1);
            Assert.Contains(written, row => row.Level == "Information" && row.Message == "normal exit" && row.Occurrences == 1);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedWriterCoalescesBurstsWithinCapacityAndReportsLoss()
    {
        var entered = Signal(); var release = Signal();
        var written = new ConcurrentQueue<DiagnosticLogRecord>();
        var time = new ManualTimeProvider();
        await using var queue = new DiagnosticPersistenceQueue(async (batch, _) =>
        {
            entered.TrySetResult(); await release.Task;
            foreach (var row in batch) written.Enqueue(row);
        }, new SupervisionClock(time), capacity: 4, batchSize: 1);
        try
        {
            Assert.True(queue.TryEnqueue("in-flight", "Information", "Fixture", "first"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 10000; i++)
                Assert.True(queue.TryEnqueue("same-event", "Information", "Fixture", $"latest-{i}"));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), "Diagnostic producers waited on the blocked writer.");
            Assert.True(queue.TryEnqueue("other-1", "Warning", "Fixture", "other"));
            Assert.True(queue.TryEnqueue("other-2", "Warning", "Fixture", "other"));
            Assert.False(queue.TryEnqueue("over-capacity", "Warning", "Fixture", "lost"));
            Assert.Equal(4, queue.Status.Pending);
            Assert.Equal(4, queue.Status.PeakPending);
            Assert.Equal(9999, queue.Status.Coalesced);
            Assert.Equal(1, queue.Status.Dropped);
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(2), queue.Status.OldestAge);
            release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => queue.FlushAsync());
            Assert.Equal(4, written.Count);
            var coalesced = Assert.Single(written, row => row.Message == "latest-9999");
            Assert.Equal(10000, coalesced.Occurrences);
            Assert.False(queue.Status.IsHealthy);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task FlushCoversOldCoalescedEntryButNotLaterAdmission()
    {
        var a = Signal(); var b = Signal(); var c = Signal();
        var allowA = Signal(); var allowB = Signal(); var allowC = Signal();
        await using var queue = new DiagnosticPersistenceQueue(async (batch, _) =>
        {
            switch (Assert.Single(batch).Message)
            {
                case "a": a.TrySetResult(); await allowA.Task; break;
                case "b-new": b.TrySetResult(); await allowB.Task; break;
                case "c": c.TrySetResult(); await allowC.Task; break;
                default: Assert.Fail("Unexpected summary record"); break;
            }
        }, capacity: 4, batchSize: 1);
        try
        {
            queue.TryEnqueue("a", "Information", "Fixture", "a");
            await a.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.TryEnqueue("b", "Information", "Fixture", "b-old");
            var flush = queue.FlushAsync();
            queue.TryEnqueue("c", "Information", "Fixture", "c");
            queue.TryEnqueue("b", "Information", "Fixture", "b-new");
            allowA.TrySetResult();
            await b.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(flush.IsCompleted, "Coalescing lost the old record's flush coverage.");
            allowB.TrySetResult();
            await c.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await flush.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, queue.Status.Pending);
        }
        finally { allowA.TrySetResult(); allowB.TrySetResult(); allowC.TrySetResult(); }
    }

    [Fact]
    public async Task FailedWriteIsNotReportedAsDurableAndLaterRecordsStillDrain()
    {
        var fail = true;
        var written = new ConcurrentQueue<DiagnosticLogRecord>();
        await using var queue = new DiagnosticPersistenceQueue((batch, _) =>
        {
            if (fail) throw new IOException("Injected disk failure");
            foreach (var row in batch) written.Enqueue(row);
            return Task.CompletedTask;
        });
        queue.TryEnqueue("a", "Warning", "Fixture", "failed");
        await Assert.ThrowsAsync<IOException>(() => queue.FlushAsync());
        Assert.Equal(0, queue.Status.Written);
        Assert.Equal(1, queue.Status.WriteFailures);
        fail = false;
        queue.TryEnqueue("b", "Information", "Fixture", "committed");
        await Assert.ThrowsAsync<IOException>(() => queue.FlushAsync());
        Assert.Equal("committed", Assert.Single(written).Message);
        Assert.Equal(1, queue.Status.Written);
        Assert.False(queue.Status.IsHealthy);
    }

    [Fact]
    public async Task CancelledFlushWaitersDoNotAccumulateOrCancelWriter()
    {
        var release = Signal();
        await using var queue = new DiagnosticPersistenceQueue(async (_, _) => await release.Task);
        try
        {
            queue.TryEnqueue("a", "Information", "Fixture", "held");
            for (var i = 0; i < 100; i++)
            {
                using var cancellation = new CancellationTokenSource();
                var flush = queue.FlushAsync(cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
            }
            Assert.Equal(1, queue.Status.Pending);
            release.TrySetResult();
            await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(queue.Status.IsHealthy);
        }
        finally { release.TrySetResult(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
