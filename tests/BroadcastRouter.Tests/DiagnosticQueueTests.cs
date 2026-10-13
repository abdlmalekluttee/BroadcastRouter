using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class DiagnosticQueueTests
{
    [Fact]
    public async Task SmallLifecycleBurstCoalescesBeforeOpeningDurableTransaction()
    {
        var time = new ObservedBatchClock();
        var entered = Signal();
        var writes = new ConcurrentQueue<DiagnosticLogRecord[]>();
        await using var queue = new DiagnosticPersistenceQueue((batch, _) =>
        { writes.Enqueue(batch.ToArray()); entered.TrySetResult(); return Task.CompletedTask; },
            new SupervisionClock(time), coalescingInterval: TimeSpan.FromMilliseconds(100));
        queue.TryEnqueue("exit", "Information", "Synthetic", "first");
        await Task.WhenAny(time.TimerCreated.Task, entered.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(entered.Task.IsCompleted, "A small diagnostic burst opened a transaction before its coalescing window.");
        for (var i = 0; i < 19; i++) queue.TryEnqueue("exit", "Information", "Synthetic", "latest");
        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Empty(writes);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await queue.FlushAsync();
        var record = Assert.Single(Assert.Single(writes));
        Assert.Equal(20, record.Occurrences);
        Assert.Equal("latest", record.Message);
        Assert.Equal(20, queue.Status.Written);
    }

    [Theory]
    [InlineData("flush")]
    [InlineData("full-batch")]
    [InlineData("shutdown")]
    public async Task BarriersAndFullBatchesWakeCoalescingWithoutWaitingForClock(string action)
    {
        var time = new ObservedBatchClock();
        var entered = Signal();
        var writes = new ConcurrentQueue<DiagnosticLogRecord>();
        await using var queue = new DiagnosticPersistenceQueue((batch, _) =>
        { foreach (var item in batch) writes.Enqueue(item); entered.TrySetResult(); return Task.CompletedTask; },
            new SupervisionClock(time), batchSize: 4, coalescingInterval: TimeSpan.FromMilliseconds(100));
        queue.TryEnqueue("first", "Information", "Synthetic", "first");
        await Task.WhenAny(time.TimerCreated.Task, entered.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(entered.Task.IsCompleted, "The diagnostic coalescing window was not entered.");
        if (action == "flush") await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        else if (action == "shutdown") await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        else
        {
            for (var i = 0; i < 3; i++) queue.TryEnqueue("other-" + i, "Information", "Synthetic", "other");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await queue.FlushAsync();
        }
        Assert.Equal(action == "full-batch" ? 4 : 1, writes.Count);
        Assert.Equal(0, queue.Status.Pending);
        Assert.Equal(0, queue.Status.WriteFailures);
        Assert.Equal(0, queue.Status.Abandoned);
    }

    private sealed class ObservedBatchClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        internal TaskCompletionSource TimerCreated { get; } = Signal();
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);
    }

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
