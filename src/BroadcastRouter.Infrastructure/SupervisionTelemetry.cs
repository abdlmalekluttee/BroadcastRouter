using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BroadcastRouter.Application;

namespace BroadcastRouter.Infrastructure;

public sealed record SupervisionEvidence(bool? ProcessAlive = null, long? Frame = null,
    long? MediaTimeMicroseconds = null, long? DuplicatedFrames = null, long? DroppedFrames = null,
    double? ProgressAgeMilliseconds = null, string? FailureCategory = null, string? RouteState = null,
    bool? PublisherConnected = null, int? ExitCode = null,
    int? AudioStarvationWarnings = null, int? VideoStarvationWarnings = null, int? AudioDecodeErrors = null,
    int? AudioTimestampDiscontinuities = null, int? VideoTimestampDiscontinuities = null,
    double? FirstAnomalyAgeMilliseconds = null, double? LastAnomalyAgeMilliseconds = null);

public sealed record SupervisionDecisionRecord(DateTimeOffset Utc, long MonotonicTicks, Guid ServiceRunId,
    string RouteId, string? OutputId, long SourceEpoch, Guid? SessionId, int? ProcessId,
    DateTimeOffset? ProcessStartUtc, string Trigger, SupervisionEvidence Before, SupervisionEvidence? After,
    string Threshold, double? WindowMilliseconds, IReadOnlyList<string> SuppressedAlternatives,
    string Action, string Result, string? StderrLog, Guid DecisionId, double? DurationMilliseconds = null);

public sealed record TelemetryStatus(long Accepted, long Written, long Dropped, long WriteFailures,
    long AbandonedAtShutdown, long CapacityEvictions, long ExpiredFiles, DateTimeOffset? OldestRetainedSegmentWriteUtc)
{
    public bool IsHealthy => Dropped == 0 && WriteFailures == 0 && AbandonedAtShutdown == 0 && CapacityEvictions == 0;
    public bool RetentionComplete => IsHealthy;
}

/// <summary>
/// Non-blocking, bounded diagnostics. No disk work runs in media/route callbacks.
/// Retention is at most 30 days, additionally bounded by file count and bytes.
/// A full queue or failed disk write is explicit in Status, never silently a
/// claim of complete evidence. This component never authorizes media recovery.
/// </summary>
public sealed class SupervisionTelemetry : IAsyncDisposable
{
    // 128 GiB maximum occupied segment capacity; not preallocated. Sized from
    // tools/TelemetryLoad's full synthetic churn fixture (see P2-03 retention evidence).
    public const int DefaultSegmentBytes = 16 * 1024 * 1024;
    public const int DefaultMaxSegments = 8192;
    private readonly string _root;
    private readonly SupervisionClock _clock;
    private readonly int _maxFileBytes;
    private readonly int _maxFiles;
    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private readonly Channel<Pending> _queue;
    private readonly Task _worker;
    private readonly CancellationTokenSource _shutdown = new();
    internal TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal Func<Task>? BeforeWriteForTesting { get; init; }
    internal Task WorkerCompletionForTesting => _worker;
    private readonly Dictionary<string, Segment> _segments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (bool Connected, long Epoch)> _epochs = new(StringComparer.Ordinal);
    private long _accepted, _written, _dropped, _writeFailures;
    private long _pendingRecords, _abandoned, _capacityEvictions, _expiredFiles, _oldestRetainedTicks;
    private long _sequence;
    private long _lastPrune;
    private bool _retentionStateLoaded;
    private bool _disposed;
    public Guid ServiceRunId { get; } = Guid.NewGuid();
    public TelemetryStatus Status => new(Interlocked.Read(ref _accepted), Interlocked.Read(ref _written),
        Interlocked.Read(ref _dropped), Interlocked.Read(ref _writeFailures), Interlocked.Read(ref _abandoned),
        Interlocked.Read(ref _capacityEvictions), Interlocked.Read(ref _expiredFiles),
        Interlocked.Read(ref _oldestRetainedTicks) is var ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);

    public SupervisionTelemetry(string root, SupervisionClock? clock = null,
        int maxFileBytes = DefaultSegmentBytes, int maxFiles = DefaultMaxSegments, int queueCapacity = 4096)
    {
        if (maxFileBytes < 16384 || maxFiles < 2 || queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        _root = Path.GetFullPath(root);
        _clock = clock ?? SupervisionClock.System;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        RejectReparsePath(_root);
        Directory.CreateDirectory(_root);
        RejectReparsePath(_root);
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _lastPrune = _clock.GetTimestamp();
        _worker = Task.Run(ConsumeAsync);
    }

    public string OpaqueId(string raw) => Convert.ToHexString(HMACSHA256.HashData(_identityKey, Encoding.UTF8.GetBytes(raw)))[..24];
    // This identifies shared segments, not an unfiltered session transcript.
    // Reconstruct by ServiceRunId + SessionId and order by envelope Sequence.
    public string StderrReference(Guid session) => $"run-{ServiceRunId:N}.stderr.*.jsonl";
    public long SourceEpoch(string source) => _epochs.TryGetValue(source, out var value) ? value.Epoch : 0;

    public void ObservePublisher(string source, bool connected)
    {
        // Observational epochs only; existing publisher/retry policy is untouched.
        var previous = _epochs.GetValueOrDefault(source);
        var current = _epochs.AddOrUpdate(source, (connected, connected ? 1 : 0), (_, old) =>
            (connected, connected && !old.Connected ? old.Epoch + 1 : old.Epoch));
        if (current != previous)
            Record(source, null, null, "PublisherObservation", new(PublisherConnected: previous.Connected),
                new(PublisherConnected: connected), "REST observation; not media proof", null, [], "Observe", "Recorded");
    }

    public Guid Record(string source, string? output, RouteProcessSnapshot? process, string trigger,
        SupervisionEvidence before, SupervisionEvidence? after, string threshold, double? windowMilliseconds,
        IReadOnlyList<string> suppressedAlternatives, string action, string result, Guid? decisionId = null,
        double? durationMilliseconds = null)
    {
        var id = decisionId ?? Guid.NewGuid();
        var record = new SupervisionDecisionRecord(_clock.UtcNow, _clock.GetTimestamp(), ServiceRunId,
            OpaqueId(source), output is null ? null : OpaqueId(output), SourceEpoch(source),
            process?.SessionId, process?.ProcessId, process?.ProcessStartUtc,
            Safe(trigger), Clean(before), after is null ? null : Clean(after), Safe(threshold), windowMilliseconds,
            suppressedAlternatives.Select(Safe).ToArray(), Safe(action), Safe(result), process?.StderrLog, id, durationMilliseconds);
        Enqueue($"run-{ServiceRunId:N}.decisions", record);
        return id;
    }

    public DecisionTrace BeginDecision(string source, string? output, RouteProcessSnapshot? process,
        string trigger, string threshold, double? windowMilliseconds, string[] suppressedAlternatives, string action,
        string? routeState = null) => new(this, source, output, process, trigger, threshold, windowMilliseconds, suppressedAlternatives, action, routeState);

    public sealed class DecisionTrace : IDisposable
    {
        private readonly SupervisionTelemetry _owner;
        private readonly string _source, _trigger, _threshold, _action;
        private readonly string? _output;
        private readonly RouteProcessSnapshot? _process;
        private readonly SupervisionEvidence _before;
        private readonly double? _window;
        private readonly string[] _suppressed;
        private readonly Guid _id;
        private bool _completed;
        internal DecisionTrace(SupervisionTelemetry owner, string source, string? output, RouteProcessSnapshot? process,
            string trigger, string threshold, double? window, string[] suppressed, string action, string? state)
        {
            _owner = owner; _source = source; _output = output; _process = process; _trigger = trigger;
            _threshold = threshold; _window = window; _suppressed = suppressed; _action = action;
            _before = Evidence(process, owner._clock, state);
            _id = owner.Record(source, output, process, trigger, _before, null, threshold, window, suppressed, action, "Requested");
        }
        public void Complete(RouteProcessSnapshot? after, string result, string? routeState = null)
        {
            if (_completed) return;
            _completed = true;
            _owner.Record(_source, _output, after ?? _process, _trigger, _before, Evidence(after, _owner._clock, routeState),
                _threshold, _window, _suppressed, _action, result, _id);
        }
        public void Dispose()
        {
            if (!_completed) Complete(null, "AbortedOrFailed");
        }
    }

    public void Stderr(Guid session, string line) => Enqueue($"run-{ServiceRunId:N}.stderr",
        new { Utc = _clock.UtcNow, MonotonicTicks = _clock.GetTimestamp(), ServiceRunId, SessionId = session,
            Text = Safe(line) });

    public void RouterLog(string level, string category, string message) => Enqueue($"run-{ServiceRunId:N}.router",
        new { Utc = _clock.UtcNow, MonotonicTicks = _clock.GetTimestamp(), ServiceRunId,
            Level = Safe(level), Category = Safe(category), Message = Safe(message) });

    public static SupervisionEvidence Evidence(RouteProcessSnapshot? process, SupervisionClock clock, string? state = null) =>
        new(process?.Running, process?.Progress?.Frame,
            process?.Progress?.OutputTime is { } media ? (long)(media.TotalMilliseconds * 1000) : null,
            process?.Progress?.DuplicatedFrames, process?.Progress?.DroppedFrames,
            process?.Progress is { } progress ? (clock.Now - progress.LastProgressAt).TotalMilliseconds : null,
            process?.InputFailure?.Category, state, ExitCode: process?.ExitCode,
            AudioStarvationWarnings: process?.MediaHealth.AudioStarvationWarnings,
            VideoStarvationWarnings: process?.MediaHealth.VideoStarvationWarnings,
            AudioDecodeErrors: process?.MediaHealth.AudioDecodeErrors,
            AudioTimestampDiscontinuities: process?.MediaHealth.AudioTimestampDiscontinuities,
            VideoTimestampDiscontinuities: process?.MediaHealth.VideoTimestampDiscontinuities,
            FirstAnomalyAgeMilliseconds: process?.MediaHealth.FirstAnomalyAt is { } first ? (clock.Now - first).TotalMilliseconds : null,
            LastAnomalyAgeMilliseconds: process?.MediaHealth.LastAnomalyAt is { } last ? (clock.Now - last).TotalMilliseconds : null);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(null, null, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.Add(ref _abandoned, Interlocked.Read(ref _pendingRecords));
            _shutdown.Cancel(); // Never wait indefinitely for a wedged filesystem to acknowledge cancellation.
        }
        CryptographicOperations.ZeroMemory(_identityKey);
    }

    private static string Safe(string value) => TelemetryRedactor.Redact(value);
    private static SupervisionEvidence Clean(SupervisionEvidence value) => value with
    { FailureCategory = value.FailureCategory is null ? null : Safe(value.FailureCategory),
        RouteState = value.RouteState is null ? null : Safe(value.RouteState) };

    private void Enqueue(string stem, object value)
    {
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var json = JsonSerializer.Serialize(new { Sequence = sequence, TimestampFrequency = _clock.TimestampFrequency, Telemetry = Status, Event = value });
            Interlocked.Increment(ref _pendingRecords);
            if (!_queue.Writer.TryWrite(new(stem, json + "\n", null)))
            { Interlocked.Decrement(ref _pendingRecords); Interlocked.Increment(ref _dropped); }
            else Interlocked.Increment(ref _accepted);
        }
        catch { Interlocked.Increment(ref _dropped); }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_shutdown.IsCancellationRequested)
                { item.Flush?.TrySetCanceled(); continue; }
                if (item.Flush is not null) { Prune(); item.Flush.TrySetResult(); continue; }
                if (BeforeWriteForTesting is { } beforeWrite) await beforeWrite().ConfigureAwait(false);
                _shutdown.Token.ThrowIfCancellationRequested();
                RejectReparsePath(_root);
                var bytes = Encoding.UTF8.GetBytes(item.Json!);
                if (bytes.Length > _maxFileBytes) throw new IOException("Telemetry record exceeds segment limit.");
                if (!_segments.TryGetValue(item.Stem!, out var segment) || segment.Length + bytes.Length > _maxFileBytes)
                {
                    if (_segments.Count >= 64) _segments.Remove(_segments.Keys.First());
                    segment = new(Path.Combine(_root, item.Stem + "." + Guid.NewGuid().ToString("N") + ".jsonl"));
                    _segments[item.Stem!] = segment;
                    // Bound the directory before creating another segment.
                    Prune(reserveFile: true);
                }
                RejectReparsePath(segment.Path);
                using (var stream = new FileStream(segment.Path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    await stream.WriteAsync(bytes, _shutdown.Token).ConfigureAwait(false);
                segment.Length += bytes.Length;
                File.SetLastWriteTimeUtc(segment.Path, _clock.UtcNow.UtcDateTime);
                Interlocked.Increment(ref _written);
                if (_clock.GetElapsedTime(_lastPrune) >= TimeSpan.FromMinutes(1)) Prune();
            }
            catch
            {
                Interlocked.Increment(ref _writeFailures);
                item.Flush?.TrySetException(new IOException("Telemetry storage is degraded; inspect disk access/capacity."));
            }
            finally { if (item.Flush is null) Interlocked.Decrement(ref _pendingRecords); }
        }
    }

    private void Prune(bool reserveFile = false)
    {
        RejectReparsePath(_root);
        LoadRetentionState();
        var files = Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(path => IsOwnedName(Path.GetFileName(path))).Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        var keep = _maxFiles - (reserveFile ? 1 : 0);
        long oldest = 0;
        foreach (var file in files.Select((value, index) => (value, index)))
        {
            var expired = _clock.UtcNow - new DateTimeOffset(file.value.LastWriteTimeUtc, TimeSpan.Zero) >= TimeSpan.FromDays(30);
            if (file.index < keep && !expired)
            { oldest = file.value.LastWriteTimeUtc.Ticks; continue; }
            RejectReparsePath(file.value.FullName);
            // Persist the alarm before deleting evidence, so a crash/restart
            // cannot make an incomplete history look complete again.
            if (!expired) SaveRetentionLoss(checked(Interlocked.Read(ref _capacityEvictions) + 1));
            File.Delete(file.value.FullName); // Only validated telemetry names within this dedicated directory.
            if (expired) Interlocked.Increment(ref _expiredFiles);
            else Interlocked.Increment(ref _capacityEvictions);
            foreach (var key in _segments.Where(pair => pair.Value.Path == file.value.FullName).Select(pair => pair.Key).ToArray())
                _segments.Remove(key);
        }
        Interlocked.Exchange(ref _oldestRetainedTicks, oldest);
        _lastPrune = _clock.GetTimestamp();
    }

    private static bool IsOwnedName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"^(?:run-[a-f0-9]{32}\.(?:decisions|router|stderr)|session-[a-f0-9]{32}-[a-f0-9]{32}\.stderr)\.[a-f0-9]{32}\.jsonl$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private void LoadRetentionState()
    {
        if (_retentionStateLoaded) return;
        var path = Path.Combine(_root, "retention-loss.json");
        RejectReparsePath(path);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4096) throw new IOException("Invalid retention state.");
            var state = JsonSerializer.Deserialize<RetentionLoss>(File.ReadAllText(path));
            if (state is null || state.CapacityEvictions < 1) throw new IOException("Invalid retention state.");
            Interlocked.Exchange(ref _capacityEvictions, state.CapacityEvictions);
        }
        _retentionStateLoaded = true;
    }

    private void SaveRetentionLoss(long count)
    {
        var path = Path.Combine(_root, "retention-loss.json");
        var temporary = Path.Combine(_root, "retention-loss.tmp");
        RejectReparsePath(path);
        RejectReparsePath(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new RetentionLoss(count, _clock.UtcNow));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record RetentionLoss(long CapacityEvictions, DateTimeOffset LastLossUtc);

    private static void RejectReparsePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Telemetry paths cannot contain reparse points.");
    }

    private sealed record Pending(string? Stem, string? Json, TaskCompletionSource? Flush);
    private sealed class Segment(string path) { public string Path { get; } = path; public long Length { get; set; } }
}
