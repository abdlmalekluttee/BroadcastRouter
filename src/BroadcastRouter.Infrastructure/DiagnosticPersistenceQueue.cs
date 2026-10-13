using BroadcastRouter.Application;

namespace BroadcastRouter.Infrastructure;

public sealed record DiagnosticLogRecord(DateTimeOffset FirstUtc, DateTimeOffset LastUtc,
    string Level, string Category, string Message, string? SourceId, string? CorrelationId, long Occurrences = 1);

public sealed record DiagnosticPersistenceStatus(int Pending, int PeakPending, long Accepted, long Written,
    long Coalesced, long Dropped, long WriteFailures, long Abandoned, TimeSpan OldestAge,
    TimeSpan LastTransaction, TimeSpan MaxTransaction, long RetentionEvictions = 0, long MaintenanceFailures = 0,
    bool HistoricalIncomplete = false)
{
    public bool IsHealthy => Dropped == 0 && WriteFailures == 0 && Abandoned == 0
        && RetentionEvictions == 0 && MaintenanceFailures == 0 && !HistoricalIncomplete && OldestAge < TimeSpan.FromSeconds(5);
}

/// <summary>
/// Optional diagnostic summaries only. Essential state must never use this queue.
/// One bounded worker, no per-event Tasks, and no producer-side database I/O.
/// Detailed per-session lifecycle evidence remains in SupervisionTelemetry.
/// </summary>
public sealed class DiagnosticPersistenceQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<DiagnosticLogRecord>, CancellationToken, Task> _write;
    private readonly SupervisionClock _clock;
    private readonly int _capacity, _batchSize;
    private readonly TimeSpan _coalescingInterval;
    private readonly LinkedList<Pending> _pending = [];
    private readonly Dictionary<(string Key, string Level, string Category, string? Source), LinkedListNode<Pending>> _byKey = [];
    private readonly List<Barrier> _barriers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Pending[] _inFlight = [];
    private Task _worker = Task.CompletedTask;
    private TaskCompletionSource? _batchWake;
    private bool _active, _closed;
    private long _sequence, _accepted, _written, _coalesced, _dropped, _failed, _abandoned;
    private int _peak;
    private TimeSpan _lastTransaction, _maxTransaction;

    public DiagnosticPersistenceQueue(Func<IReadOnlyList<DiagnosticLogRecord>, CancellationToken, Task> write,
        SupervisionClock? clock = null, int capacity = 2048, int batchSize = 32, TimeSpan? coalescingInterval = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (capacity < 1 || batchSize < 1 || batchSize > capacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _coalescingInterval = coalescingInterval ?? TimeSpan.Zero;
        if (_coalescingInterval < TimeSpan.Zero || _coalescingInterval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(coalescingInterval));
        _write = write; _clock = clock ?? SupervisionClock.System; _capacity = capacity; _batchSize = batchSize;
    }

    public DiagnosticPersistenceStatus Status
    {
        get
        {
            lock (_gate)
            {
                var oldest = _inFlight.FirstOrDefault() ?? _pending.First?.Value;
                return new(_pending.Count + _inFlight.Length, _peak, _accepted, _written, _coalesced,
                    _dropped, _failed, _abandoned, oldest is null ? TimeSpan.Zero : _clock.GetElapsedTime(oldest.Timestamp),
                    _lastTransaction, _maxTransaction);
            }
        }
    }

    public bool TryEnqueue(string key, string level, string category, string message,
        string? sourceId = null, string? correlationId = null)
    {
        lock (_gate)
        {
            // Bound bytes as well as record count, including attacker-controlled
            // identifiers. Reject rather than silently truncate a forensic record.
            if (_closed || key.Length > 2048 || level.Length > 32 || category.Length > 128
                || message.Length > 8192 || sourceId?.Length > 1024 || correlationId?.Length > 256)
            { _dropped++; return false; }
            var now = _clock.UtcNow;
            var sequence = ++_sequence;
            var scopedKey = (key, level, category, sourceId);
            if (_byKey.TryGetValue(scopedKey, out var node))
            {
                node.Value.Record = node.Value.Record with
                { LastUtc = now, Message = message, CorrelationId = correlationId, Occurrences = node.Value.Record.Occurrences + 1 };
                _coalesced++; _accepted++;
                return true; // Keep FirstSequence: an earlier flush still covers this summary.
            }
            if (_pending.Count + _inFlight.Length >= _capacity) { _dropped++; return false; }
            var pending = new Pending(scopedKey, sequence, _clock.GetTimestamp(), new(now, now, level, category, message, sourceId, correlationId));
            _byKey.Add(scopedKey, _pending.AddLast(pending));
            _accepted++;
            _peak = Math.Max(_peak, _pending.Count + _inFlight.Length);
            if (_pending.Count >= _batchSize) _batchWake?.TrySetResult();
            if (!_active) { _active = true; _worker = Task.Run(DrainAsync); }
            return true;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Barrier? barrier = null;
        lock (_gate)
        {
            if (!HasPrior(_sequence)) { ThrowIfIncomplete(); return; }
            if (_barriers.Count >= 64) throw new InvalidOperationException("Too many concurrent diagnostic flush requests.");
            barrier = new(_sequence);
            _barriers.Add(barrier);
            _batchWake?.TrySetResult();
        }
        try { await barrier.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { lock (_gate) _barriers.Remove(barrier); }
    }

    private bool HasPrior(long sequence) => _inFlight.Any(item => item.FirstSequence <= sequence)
        || _pending.Any(item => item.FirstSequence <= sequence);
    private void ThrowIfIncomplete()
    {
        if (_failed > 0 || _dropped > 0 || _abandoned > 0)
            throw new IOException("Diagnostic summaries are incomplete; inspect persistence health.");
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Pending[] batch;
            TaskCompletionSource? wake = null;
            lock (_gate)
            {
                if (_pending.Count == 0) { _active = false; CompleteBarriers(); return; }
                // Aggregate small bursts before opening a durable transaction.
                // Full batches, flush barriers and shutdown never wait for this
                // optional window. One timer per batch, not one task per event.
                if (_coalescingInterval > TimeSpan.Zero && _pending.Count < _batchSize && !_closed && _barriers.Count == 0)
                    wake = _batchWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (wake is not null)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var delay = _clock.DelayAsync(_coalescingInterval, wait.Token);
                await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
                wait.Cancel();
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                lock (_gate) _batchWake = null;
            }
            lock (_gate)
            {
                batch = _pending.Take(_batchSize).ToArray();
                foreach (var item in batch) { _pending.RemoveFirst(); _byKey.Remove(item.Key); }
                _inFlight = batch;
            }
            var started = _clock.GetTimestamp();
            var success = false;
            try
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                await _write(batch.Select(item => item.Record).ToArray(), _shutdown.Token).ConfigureAwait(false);
                success = true;
            }
            catch (Exception) { /* Explicit failed counters below; never acknowledge these rows. */ }
            lock (_gate)
            {
                var count = batch.Sum(item => item.Record.Occurrences);
                if (success) _written += count; else _failed += count;
                _lastTransaction = _clock.GetElapsedTime(started);
                if (_lastTransaction > _maxTransaction) _maxTransaction = _lastTransaction;
                _inFlight = [];
                CompleteBarriers();
            }
        }
    }

    private void CompleteBarriers()
    {
        foreach (var barrier in _barriers.Where(value => !HasPrior(value.Sequence)).ToArray())
        {
            if (_failed > 0 || _dropped > 0 || _abandoned > 0)
                barrier.Completion.TrySetException(new IOException("Diagnostic summaries are incomplete; inspect persistence health."));
            else barrier.Completion.TrySetResult();
            _barriers.Remove(barrier);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate) { _closed = true; _batchWake?.TrySetResult(); worker = _worker; }
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                _abandoned = _pending.Sum(item => item.Record.Occurrences) + _inFlight.Sum(item => item.Record.Occurrences);
                foreach (var barrier in _barriers)
                    barrier.Completion.TrySetException(new IOException("Diagnostic shutdown deadline expired."));
                _barriers.Clear();
            }
            _shutdown.Cancel(); // One worker may still be in uncooperative storage; never spawn a replacement.
        }
    }

    private sealed class Pending((string Key, string Level, string Category, string? Source) key, long firstSequence, long timestamp, DiagnosticLogRecord record)
    {
        public (string Key, string Level, string Category, string? Source) Key { get; } = key;
        public long FirstSequence { get; } = firstSequence;
        public long Timestamp { get; } = timestamp;
        public DiagnosticLogRecord Record { get; set; } = record;
    }
    private sealed class Barrier(long sequence)
    {
        public long Sequence { get; } = sequence;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
