using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record RouteProcessSnapshot(
    SourceIdentity Source,
    RouteProcessPurpose Purpose,
    int ProcessId,
    DateTimeOffset StartedAt,
    bool Running,
    FfmpegProgressSnapshot? Progress,
    IReadOnlyList<string> RecentErrors,
    int? ExitCode,
    FfmpegInputFailure? InputFailure,
    FfmpegMediaHealthSnapshot MediaHealth,
    Guid SessionId = default, DateTimeOffset? ProcessStartUtc = null, string? StderrLog = null,
    bool StopRequested = false,
    MediaEvidenceSnapshot? IndependentMedia = null,
    MediaProperties? SelectedMedia = null);

public enum RouteProcessPurpose { Live, Fallback, PortStandby }

public enum RouteProcessLifecycleState { Started, StopRequested, ForcedTermination, Exited }

public sealed record RouteProcessLifecycleEvent(
    SourceIdentity Source,
    int ProcessId,
    RouteProcessLifecycleState State,
    DateTimeOffset Timestamp,
    int? ExitCode = null);

public sealed class FfmpegProcessSupervisor(
    FfmpegRouteOptions options,
    TimeSpan gracefulStopTimeout,
    SupervisionClock? clock = null, SupervisionTelemetry? telemetry = null) : IRouteProcessSupervisor, IAsyncDisposable
{
    private readonly SupervisionClock _clock = clock ?? SupervisionClock.System;
    private static readonly TimeSpan ProcessReapTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuitSignalTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MediaStarvationStartupGrace = TimeSpan.FromSeconds(5);
    internal const int RetainedExitedOwners = 256;
    private readonly ConcurrentDictionary<string, ManagedProcess> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteProcessSnapshot> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new(StringComparer.Ordinal);
    private readonly WindowsKillOnCloseJob _containmentJob = WindowsKillOnCloseJob.Create();

    public event Action<RouteProcessLifecycleEvent>? LifecycleChanged;

    public Task StartAsync(RouteRecord route, DiscoveredSource source, DeckLinkPort port, OutputPreset preset, CancellationToken cancellationToken) =>
        StartAsync(route, source, port, preset, cancellationToken, options.UseIndependentMediaStats);

    public async Task StartAsync(RouteRecord route, DiscoveredSource source, DeckLinkPort port, OutputPreset preset,
        CancellationToken cancellationToken, bool useIndependentMediaStats)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = FfmpegCommandBuilder.Build(options, source, port, preset);
        var media = useIndependentMediaStats ? new FfmpegMediaSession(source.Media, preset.IncludeAudio, _clock) : null;
        try
        {
            if (media is not null) await media.AttachToAsync(start).ConfigureAwait(false);
            await StartProcessAsync(source.Identity, RouteProcessPurpose.Live, start, cancellationToken, port.StableId, media,
                source.Media is null ? null : source.Media with { AudioDisabledByPreset = !preset.IncludeAudio }).ConfigureAwait(false);
        }
        catch
        {
            if (media is not null) await media.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task StartFallbackAsync(SourceIdentity source, DeckLinkPort port, OutputPreset preset, FallbackMode mode, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(source, RouteProcessPurpose.Fallback,
            FfmpegCommandBuilder.BuildFallback(options, port, preset, mode, value), cancellationToken, port.StableId);
    }

    public Task StartPortStandbyAsync(SourceIdentity owner, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(owner, RouteProcessPurpose.PortStandby,
            FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration), cancellationToken, port.StableId);
    }

    public Task StartRecoveryStandbyAsync(SourceIdentity source, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(source, RouteProcessPurpose.Fallback,
            FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration), cancellationToken, port.StableId);
    }

    public Task StopAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        StopOwnedAsync(source, gracefulStopTimeout, cancellationToken);

    public Task StopForOutputHandoffAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        // Handoffs must allow the same qualified shutdown budget as normal stops.
        // An optimistic short deadline cannot make a device release gracefully.
        StopOwnedAsync(source, gracefulStopTimeout, cancellationToken);

    private async Task StopOwnedAsync(SourceIdentity source, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        var ownerGate = OwnerGate(source.Value);
        await ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_running.TryGetValue(source.Value, out var managed)) return;
            Interlocked.Exchange(ref managed.StopRequested, 1);
            try
            {
                await StopManagedAsync(managed, stopTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!HasExited(managed.Process)) Interlocked.Exchange(ref managed.StopRequested, 0);
                throw;
            }
            finally
            {
                // Once the owned process has exited it must never continue blocking this
                // source identity, even if stream draining, lifecycle reporting, or disposal
                // failed during teardown.
                if (HasExited(managed.Process))
                    _running.TryRemove(new KeyValuePair<string, ManagedProcess>(source.Value, managed));
            }
        }
        finally { ownerGate.Release(); }
    }

    public IReadOnlyList<RouteProcessSnapshot> Snapshot()
    {
        var live = _running.Values.Select(CreateSnapshot);
        var liveIds = new HashSet<string>(_running.Keys, StringComparer.Ordinal);
        return live.Concat(_last.Where(pair => !liveIds.Contains(pair.Key)).Select(pair => pair.Value))
            .OrderBy(item => item.Source.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<RouteProcessSnapshot> RunningSnapshot() => _running.Values
        .Select(CreateRunningSnapshot)
        .Where(snapshot => snapshot.Running)
        .ToArray();

    public bool TryGetRunning(SourceIdentity source, out RouteProcessSnapshot snapshot)
    {
        if (_running.TryGetValue(source.Value, out var managed))
        {
            var current = CreateRunningSnapshot(managed);
            if (current.Running)
            {
                snapshot = current;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public bool IsRunning(SourceIdentity source, RouteProcessPurpose purpose) =>
        TryGetRunning(source, out var snapshot) && snapshot.Purpose == purpose;

    public async ValueTask DisposeAsync()
    {
        var processes = _running.Values.ToArray();
        await Task.WhenAll(processes.Select(value => StopOwnedAsync(value.Source, gracefulStopTimeout, CancellationToken.None))).ConfigureAwait(false);
        await Task.WhenAll(processes.Select(value => value.ExitTask ?? Task.CompletedTask)).ConfigureAwait(false);
        _containmentJob.Dispose();
        foreach (var gate in _ownerGates.Values) gate.Dispose();
        _ownerGates.Clear();
    }

    internal Task StartOwnedProcessForTestingAsync(SourceIdentity source, ProcessStartInfo start,
        CancellationToken cancellationToken = default, RouteProcessPurpose purpose = RouteProcessPurpose.Live) =>
        StartProcessAsync(source, purpose, start, cancellationToken);

    private async Task StartProcessAsync(SourceIdentity source, RouteProcessPurpose purpose, ProcessStartInfo start,
        CancellationToken cancellationToken, string? outputId = null, FfmpegMediaSession? media = null, MediaProperties? selectedMedia = null)
    {
        var ownerGate = OwnerGate(source.Value);
        await ownerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_running.TryGetValue(source.Value, out var existing))
            {
                if (!HasExited(existing.Process))
                    throw new InvalidOperationException($"An FFmpeg process is already owned for {source}.");

                if (existing.Media is not null) await existing.Media.DisposeAsync().ConfigureAwait(false);
                RememberLast(CreateSnapshot(existing));
                _running.TryRemove(new KeyValuePair<string, ManagedProcess>(source.Value, existing));
                try { existing.Process.Dispose(); }
                catch { }
            }

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var managed = new ManagedProcess(source, purpose, process, _clock.Now);
            managed.Media = media;
            managed.SelectedMedia = selectedMedia;
            managed.OutputId = outputId;
            managed.StderrLog = telemetry?.StderrReference(managed.SessionId);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
                managed.ProcessId = process.Id;
                try { managed.ProcessStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { } // Exact time unknown, never substitute an invented value or change launch policy for diagnostics.
                ContainOrTerminate(process);
                // Publish only a fully associated Process. Snapshot readers do
                // not take the owner gate and must never observe the interval
                // between Process construction and Process.Start().
                if (!_running.TryAdd(source.Value, managed))
                    throw new InvalidOperationException($"An FFmpeg process is already owned for {source}.");
                managed.ProgressTask = PumpProgressAsync(managed);
                managed.ErrorTask = PumpErrorsAsync(managed);
                managed.ExitTask = ObserveExitAsync(managed);
                ReportLifecycle(managed, RouteProcessLifecycleState.Started);
            }
            catch
            {
                _running.TryRemove(new KeyValuePair<string, ManagedProcess>(source.Value, managed));
                try
                {
                    if (!HasExited(process))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }
                catch { }
                process.Dispose();
                throw;
            }
        }
        finally { ownerGate.Release(); }
    }

    private void ContainOrTerminate(Process process)
    {
        try
        {
            _containmentJob.Add(process);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch { }
            throw;
        }
    }

    private async Task ObserveExitAsync(ManagedProcess managed)
    {
        try
        {
            await managed.Process.WaitForExitAsync().ConfigureAwait(false);
            await DrainPumpsAsync(managed).ConfigureAwait(false);
            if (Volatile.Read(ref managed.StopRequested) != 0) return;
            var ownerGate = OwnerGate(managed.Source.Value);
            await ownerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref managed.StopRequested) != 0) return;
                RememberLast(CreateSnapshot(managed));
                _running.TryRemove(new KeyValuePair<string, ManagedProcess>(managed.Source.Value, managed));
                ReportLifecycle(managed, RouteProcessLifecycleState.Exited, SafeExitCode(managed.Process));
                managed.Process.Dispose();
            }
            finally { ownerGate.Release(); }
        }
        catch (Exception ex)
        {
            managed.AddError($"Supervisor observation failed: {ex.Message}");
        }
    }

    private async Task PumpProgressAsync(ManagedProcess managed)
    {
        try
        {
            while (await managed.Process.StandardOutput.ReadLineAsync(managed.PumpCancellation.Token).ConfigureAwait(false) is { } line)
            {
                var snapshot = managed.Parser.Accept(line, _clock.Now);
                if (snapshot is not null)
                {
                    managed.Progress = snapshot;
                    managed.Media?.ObserveHeartbeat();
                }
            }
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
    }

    private async Task PumpErrorsAsync(ManagedProcess managed)
    {
        try
        {
            while (await managed.Process.StandardError.ReadLineAsync(managed.PumpCancellation.Token).ConfigureAwait(false) is { } line)
            {
                var observedAt = _clock.Now;
                telemetry?.Stderr(managed.SessionId, line);
                var safe = LogRedactor.Redact(line);
                managed.AddError(safe);
                managed.MediaHealth.Observe(safe, observedAt);
                var media = managed.Media?.Snapshot(processAlive: true);
                if (FfmpegInputFailureDetector.TryClassify(safe, out var category))
                    managed.InputFailure = new(category, safe, observedAt);
                else if (managed.AudioFailureDetector.Observe(safe, observedAt, managed.StartedAt,
                             MediaStarvationStartupGrace, out var audioCategory, out var audioDetail, media))
                    managed.InputFailure = new(audioCategory, audioDetail, observedAt);
                else if (managed.MediaStarvationDetector.Observe(safe, observedAt, managed.StartedAt,
                             MediaStarvationStartupGrace, out var starvationCategory, out var starvationDetail, media))
                    managed.InputFailure = new(starvationCategory, starvationDetail, observedAt);
            }
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
    }

    private async Task StopManagedAsync(ManagedProcess managed, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        var cancellationRequested = false;
        if (!HasExited(managed.Process))
        {
            managed.StopBudgetMilliseconds = stopTimeout.TotalMilliseconds;
            ReportLifecycle(managed, RouteProcessLifecycleState.StopRequested);
            var signalStarted = _clock.GetTimestamp();
            var signalResult = "Sent";
            var signal = SignalQuitAsync(managed.Process);
            try
            {
                await signal.WaitAsync(QuitSignalTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or TimeoutException or OperationCanceledException)
            {
                signalResult = ex is OperationCanceledException ? "Cancelled" : ex.GetType().Name;
                _ = signal.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            RecordStopPhase(managed, "QuitSignal", signalStarted, QuitSignalTimeout, signalResult);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(stopTimeout);
            var exitStarted = _clock.GetTimestamp();
            try
            {
                await managed.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                RecordStopPhase(managed, "ExitWait", exitStarted, stopTimeout, "Exited");
            }
            catch (OperationCanceledException)
            {
                cancellationRequested = cancellationToken.IsCancellationRequested;
                RecordStopPhase(managed, "ExitWait", exitStarted, stopTimeout,
                    cancellationRequested ? "Cancelled" : "DeadlineExpired");
                var reapStarted = _clock.GetTimestamp();
                if (!managed.Process.HasExited)
                {
                    managed.Process.Kill(entireProcessTree: true);
                    ReportLifecycle(managed, RouteProcessLifecycleState.ForcedTermination);
                }
                using var reapDeadline = new CancellationTokenSource(ProcessReapTimeout);
                try
                {
                    await managed.Process.WaitForExitAsync(reapDeadline.Token).ConfigureAwait(false);
                    RecordStopPhase(managed, "ForcedReap", reapStarted, ProcessReapTimeout, "Exited");
                }
                catch (OperationCanceledException) when (reapDeadline.IsCancellationRequested)
                {
                    RecordStopPhase(managed, "ForcedReap", reapStarted, ProcessReapTimeout, "DeadlineExpired");
                    throw new TimeoutException($"Owned FFmpeg process {managed.ProcessId} did not exit after forced termination.");
                }
            }
        }

        var drainStarted = _clock.GetTimestamp();
        var drained = await DrainPumpsAsync(managed).ConfigureAwait(false);
        RecordStopPhase(managed, "PipeDrain", drainStarted, StreamDrainTimeout, drained ? "EOF" : "Incomplete");
        RememberLast(CreateSnapshot(managed));
        ReportLifecycle(managed, RouteProcessLifecycleState.Exited, SafeExitCode(managed.Process));
        managed.Process.Dispose();
        if (cancellationRequested) cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SignalQuitAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
        await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void RecordStopPhase(ManagedProcess managed, string phase, long started, TimeSpan budget, string result)
    {
        if (telemetry is null) return;
        var snapshot = CreateSnapshot(managed);
        telemetry.Record(managed.Source.Value, managed.OutputId, snapshot, "OwnedProcessStopPhase",
            SupervisionTelemetry.Evidence(snapshot, _clock), null, "Bounded I/O phase; not media health",
            budget.TotalMilliseconds, [], phase, result, durationMilliseconds: _clock.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<bool> DrainPumpsAsync(ManagedProcess managed)
    {
        var pumps = Task.WhenAll(managed.ProgressTask ?? Task.CompletedTask, managed.ErrorTask ?? Task.CompletedTask);
        try
        {
            await pumps.WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
            // The exit observer may already have cancelled these same readers.
            // Pumps swallow expected cancellation; completed Tasks alone do not
            // prove EOF. Incompleteness remains sticky for the owned session.
            return !managed.PumpCancellation.IsCancellationRequested;
        }
        catch (TimeoutException)
        {
            // After OS exit, readers normally finish at EOF, including buffered
            // trailer diagnostics. Cancel only a stuck reader, not every drain.
            managed.PumpCancellation.Cancel();
            managed.AddError($"Media process stream draining exceeded {StreamDrainTimeout.TotalSeconds:0} seconds.");
            _ = pumps.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // This runs only after OS exit. A failed reader must remain visible,
            // but cannot prevent exit reporting and disposal of the dead owner.
            managed.AddError(TelemetryRedactor.Redact($"Media process stream reader failed: {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // This method only drains after OS exit. Close even pipes that the
            // child never connected (e.g. an early native initialization failure).
            if (managed.Media is not null) await managed.Media.DisposeAsync().ConfigureAwait(false);
        }
        return false;
    }

    private SemaphoreSlim OwnerGate(string owner) => _ownerGates.GetOrAdd(owner, static _ => new SemaphoreSlim(1, 1));

    private void RememberLast(RouteProcessSnapshot snapshot)
    {
        _last[snapshot.Source.Value] = snapshot;
        while (_last.Count > RetainedExitedOwners)
        {
            var oldest = _last.OrderBy(pair => pair.Value.StartedAt).FirstOrDefault();
            if (oldest.Key is null || !_last.TryRemove(oldest)) break;
        }
    }

    private void ReportLifecycle(ManagedProcess managed, RouteProcessLifecycleState state, int? exitCode = null)
    {
        if (state == RouteProcessLifecycleState.Exited
            && Interlocked.Exchange(ref managed.ExitReported, 1) != 0) return;
        var snapshot = CreateSnapshot(managed);
        telemetry?.Record(managed.Source.Value, managed.OutputId, snapshot, "OwnedProcessLifecycle",
            SupervisionTelemetry.Evidence(snapshot, _clock), SupervisionTelemetry.Evidence(snapshot, _clock),
            state is RouteProcessLifecycleState.StopRequested or RouteProcessLifecycleState.ForcedTermination
                ? "Configured stop budget; owned process only" : "OS process lifecycle",
            managed.StopBudgetMilliseconds, [], state.ToString(),
            state == RouteProcessLifecycleState.ForcedTermination ? "Owned Process.Kill requested (.NET exit normally -1)"
                : exitCode == FfmpegErrorClassifier.AvErrorHttpNotFound ? "Exited: AVERROR_HTTP_NOT_FOUND"
                : exitCode is null ? "Observed" : $"Exit code {exitCode}");
        try { LifecycleChanged?.Invoke(new(managed.Source, managed.ProcessId, state, _clock.UtcNow, exitCode)); }
        catch { }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static RouteProcessSnapshot CreateSnapshot(ManagedProcess managed)
    {
        bool running;
        int? exitCode = null;
        try
        {
            running = !managed.Process.HasExited;
            if (!running) exitCode = managed.Process.ExitCode;
        }
        catch (InvalidOperationException) { running = false; }
        var observedFailure = managed.InputFailure;
        var media = managed.Media?.Snapshot(running);
        var failure = managed.RefreshInputFailure(observedFailure, media);
        return new(managed.Source, managed.Purpose, managed.ProcessId, managed.StartedAt, running, managed.Progress,
            managed.Errors.ToArray(), exitCode, failure, managed.MediaHealth.Snapshot(), managed.SessionId, managed.ProcessStartUtc, managed.StderrLog,
            Volatile.Read(ref managed.StopRequested) != 0, media, managed.SelectedMedia);
    }

    private static RouteProcessSnapshot CreateRunningSnapshot(ManagedProcess managed)
    {
        bool running;
        try { running = !managed.Process.HasExited; }
        catch (InvalidOperationException) { running = false; }
        var observedFailure = managed.InputFailure;
        var media = managed.Media?.Snapshot(running);
        var failure = managed.RefreshInputFailure(observedFailure, media);
        return new(managed.Source, managed.Purpose, managed.ProcessId, managed.StartedAt, running, managed.Progress,
            Array.Empty<string>(), null, failure, managed.MediaHealth.Snapshot(), managed.SessionId, managed.ProcessStartUtc, managed.StderrLog,
            Volatile.Read(ref managed.StopRequested) != 0, media, managed.SelectedMedia);
    }

    private sealed class ManagedProcess(SourceIdentity source, RouteProcessPurpose purpose, Process process,
        DateTimeOffset startedAt)
    {
        private const int ErrorLimit = 100;
        public Guid SessionId { get; } = Guid.NewGuid();
        public DateTimeOffset? ProcessStartUtc { get; set; }
        public string? StderrLog { get; set; }
        public string? OutputId { get; set; }
        public double? StopBudgetMilliseconds { get; set; }
        public SourceIdentity Source { get; } = source;
        public RouteProcessPurpose Purpose { get; } = purpose;
        public Process Process { get; } = process;
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAt { get; } = startedAt;
        public FfmpegProgressParser Parser { get; } = new();
        public FfmpegMediaSession? Media { get; set; }
        public MediaProperties? SelectedMedia { get; set; }
        public FfmpegMediaStarvationDetector MediaStarvationDetector { get; } = new();
        public FfmpegAudioFailureDetector AudioFailureDetector { get; } = new();
        public FfmpegMediaHealthTracker MediaHealth { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public FfmpegProgressSnapshot? Progress { get; set; }
        private FfmpegInputFailure? _inputFailure;
        public FfmpegInputFailure? InputFailure
        {
            get => Volatile.Read(ref _inputFailure);
            set => Interlocked.Exchange(ref _inputFailure, value);
        }
        public FfmpegInputFailure? RefreshInputFailure(FfmpegInputFailure? observed, MediaEvidenceSnapshot? media)
        {
            if (observed is not null && media is { Process: MediaSignalState.Healthy })
            {
                // Healthy already requires two seconds of valid advancement.
                // Missing/Unknown instrumentation cannot prove recovery. Keep
                // historical stderr, but do not retain an obsolete current fault.
                var recovered = observed.Category switch
                {
                    "AudioDecoderCorrupt" or "AudioTimestampDiscontinuous" or "DeckLinkAudioStarved" =>
                        media.Signals.Any(signal => signal.Kind == MediaSignalKind.InputAudio && signal.State == MediaSignalState.Healthy),
                    "DeckLinkVideoStarved" =>
                        media.Signals.Any(signal => signal.Kind == MediaSignalKind.InputVideo && signal.State == MediaSignalState.Healthy),
                    "RtspProtocolDesynchronized" => media.Signals.Count > 0
                        && media.Signals.All(signal => signal.State == MediaSignalState.Healthy),
                    _ => false
                };
                // A newer stderr observation must survive a snapshot that read
                // the previous fault before measuring media health.
                if (recovered) Interlocked.CompareExchange(ref _inputFailure, null, observed);
            }
            return InputFailure;
        }
        public Task? ProgressTask { get; set; }
        public Task? ErrorTask { get; set; }
        public Task? ExitTask { get; set; }
        public CancellationTokenSource PumpCancellation { get; } = new();
        public int StopRequested;
        public int ExitReported;

        public void AddError(string line)
        {
            Errors.Enqueue(line);
            while (Errors.Count > ErrorLimit) Errors.TryDequeue(out _);
        }
    }
}
