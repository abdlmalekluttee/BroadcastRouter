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
    FfmpegInputFailure? InputFailure);

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
    TimeSpan gracefulStopTimeout) : IRouteProcessSupervisor, IAsyncDisposable
{
    private static readonly TimeSpan OutputHandoffStopTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ProcessReapTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuitSignalTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MediaStarvationStartupGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MediaStarvationPairingWindow = TimeSpan.FromSeconds(3);
    internal const int RetainedExitedOwners = 256;
    private readonly ConcurrentDictionary<string, ManagedProcess> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteProcessSnapshot> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new(StringComparer.Ordinal);
    private readonly WindowsKillOnCloseJob _containmentJob = WindowsKillOnCloseJob.Create();

    public event Action<RouteProcessLifecycleEvent>? LifecycleChanged;

    public Task StartAsync(RouteRecord route, DiscoveredSource source, DeckLinkPort port, OutputPreset preset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = FfmpegCommandBuilder.Build(options, source, port, preset);
        return StartProcessAsync(source.Identity, RouteProcessPurpose.Live, start, cancellationToken);
    }

    public Task StartFallbackAsync(SourceIdentity source, DeckLinkPort port, OutputPreset preset, FallbackMode mode, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(source, RouteProcessPurpose.Fallback,
            FfmpegCommandBuilder.BuildFallback(options, port, preset, mode, value), cancellationToken);
    }

    public Task StartPortStandbyAsync(SourceIdentity owner, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(owner, RouteProcessPurpose.PortStandby,
            FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration), cancellationToken);
    }

    public Task StartRecoveryStandbyAsync(SourceIdentity source, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(source, RouteProcessPurpose.Fallback,
            FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration), cancellationToken);
    }

    public Task StopAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        StopOwnedAsync(source, gracefulStopTimeout, cancellationToken);

    public Task StopForOutputHandoffAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        StopOwnedAsync(source, OutputHandoffStopTimeout, cancellationToken);

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
        CancellationToken cancellationToken)
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

                RememberLast(CreateSnapshot(existing));
                _running.TryRemove(new KeyValuePair<string, ManagedProcess>(source.Value, existing));
                try { existing.Process.Dispose(); }
                catch { }
            }

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var managed = new ManagedProcess(source, purpose, process, DateTimeOffset.UtcNow);
            if (!_running.TryAdd(source.Value, managed))
            {
                process.Dispose();
                throw new InvalidOperationException($"An FFmpeg process is already owned for {source}.");
            }
            try
            {
                if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
                ContainOrTerminate(process);
                managed.ProgressTask = PumpProgressAsync(managed);
                managed.ErrorTask = PumpErrorsAsync(managed);
                managed.ExitTask = ObserveExitAsync(managed);
                ReportLifecycle(managed, RouteProcessLifecycleState.Started);
            }
            catch
            {
                _running.TryRemove(new KeyValuePair<string, ManagedProcess>(source.Value, managed));
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

    private static async Task PumpProgressAsync(ManagedProcess managed)
    {
        try
        {
            while (await managed.Process.StandardOutput.ReadLineAsync(managed.PumpCancellation.Token).ConfigureAwait(false) is { } line)
            {
                var snapshot = managed.Parser.Accept(line, DateTimeOffset.UtcNow);
                if (snapshot is not null) managed.Progress = snapshot;
            }
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
    }

    private static async Task PumpErrorsAsync(ManagedProcess managed)
    {
        try
        {
            while (await managed.Process.StandardError.ReadLineAsync(managed.PumpCancellation.Token).ConfigureAwait(false) is { } line)
            {
                var observedAt = DateTimeOffset.UtcNow;
                var safe = LogRedactor.Redact(line);
                managed.AddError(safe);
                if (FfmpegInputFailureDetector.TryClassify(safe, out var category))
                    managed.InputFailure = new(category, safe, observedAt);
                else if (managed.MediaStarvationDetector.Observe(safe, observedAt, managed.StartedAt,
                             MediaStarvationStartupGrace, MediaStarvationPairingWindow, out var starvationDetail))
                    managed.InputFailure = new("DeckLinkMediaStarved", starvationDetail, observedAt);
            }
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
    }

    private async Task StopManagedAsync(ManagedProcess managed, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        var cancellationRequested = false;
        if (!managed.Process.HasExited)
        {
            ReportLifecycle(managed, RouteProcessLifecycleState.StopRequested);
            var signal = SignalQuitAsync(managed.Process);
            try
            {
                await signal.WaitAsync(QuitSignalTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or TimeoutException or OperationCanceledException)
            {
                _ = signal.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(stopTimeout);
            try { await managed.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancellationRequested = cancellationToken.IsCancellationRequested;
                if (!managed.Process.HasExited)
                {
                    managed.Process.Kill(entireProcessTree: true);
                    ReportLifecycle(managed, RouteProcessLifecycleState.ForcedTermination);
                }
                using var reapDeadline = new CancellationTokenSource(ProcessReapTimeout);
                try { await managed.Process.WaitForExitAsync(reapDeadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (reapDeadline.IsCancellationRequested)
                {
                    throw new TimeoutException($"Owned FFmpeg process {managed.Process.Id} did not exit after forced termination.");
                }
            }
        }

        await DrainPumpsAsync(managed).ConfigureAwait(false);
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

    private static async Task DrainPumpsAsync(ManagedProcess managed)
    {
        managed.PumpCancellation.Cancel();
        var pumps = Task.WhenAll(managed.ProgressTask ?? Task.CompletedTask, managed.ErrorTask ?? Task.CompletedTask);
        try { await pumps.WaitAsync(StreamDrainTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            managed.AddError($"Media process stream draining exceeded {StreamDrainTimeout.TotalSeconds:0} seconds.");
            _ = pumps.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (managed.PumpCancellation.IsCancellationRequested) { }
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
        try { LifecycleChanged?.Invoke(new(managed.Source, managed.Process.Id, state, DateTimeOffset.UtcNow, exitCode)); }
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
        return new(managed.Source, managed.Purpose, managed.Process.Id, managed.StartedAt, running, managed.Progress,
            managed.Errors.ToArray(), exitCode, managed.InputFailure);
    }

    private sealed class ManagedProcess(SourceIdentity source, RouteProcessPurpose purpose, Process process,
        DateTimeOffset startedAt)
    {
        private const int ErrorLimit = 100;
        public SourceIdentity Source { get; } = source;
        public RouteProcessPurpose Purpose { get; } = purpose;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public FfmpegProgressParser Parser { get; } = new();
        public FfmpegMediaStarvationDetector MediaStarvationDetector { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public FfmpegProgressSnapshot? Progress { get; set; }
        public FfmpegInputFailure? InputFailure { get; set; }
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
