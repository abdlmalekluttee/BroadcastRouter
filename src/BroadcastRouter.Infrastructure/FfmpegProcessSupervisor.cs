using System.Collections.Concurrent;
using System.Diagnostics;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record RouteProcessSnapshot(
    SourceIdentity Source,
    int ProcessId,
    DateTimeOffset StartedAt,
    bool Running,
    FfmpegProgressSnapshot? Progress,
    IReadOnlyList<string> RecentErrors,
    int? ExitCode);

public sealed class FfmpegProcessSupervisor(
    FfmpegRouteOptions options,
    TimeSpan gracefulStopTimeout) : IRouteProcessSupervisor, IAsyncDisposable
{
    private static readonly TimeSpan OutputHandoffStopTimeout = TimeSpan.FromMilliseconds(750);
    private readonly ConcurrentDictionary<string, ManagedProcess> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteProcessSnapshot> _last = new(StringComparer.Ordinal);
    private readonly WindowsKillOnCloseJob _containmentJob = WindowsKillOnCloseJob.Create();

    public Task StartAsync(RouteRecord route, DiscoveredSource source, DeckLinkPort port, OutputPreset preset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = FfmpegCommandBuilder.Build(options, source, port, preset);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var managed = new ManagedProcess(source.Identity, process, DateTimeOffset.UtcNow);
        if (!_running.TryAdd(source.Identity.Value, managed))
        {
            process.Dispose();
            throw new InvalidOperationException($"An FFmpeg process is already owned for {source.Identity}.");
        }

        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
            ContainOrTerminate(process);
            managed.ProgressTask = PumpProgressAsync(managed);
            managed.ErrorTask = PumpErrorsAsync(managed);
            managed.ExitTask = ObserveExitAsync(managed);
            return Task.CompletedTask;
        }
        catch
        {
            _running.TryRemove(source.Identity.Value, out _);
            process.Dispose();
            throw;
        }
    }

    public Task StartFallbackAsync(SourceIdentity source, DeckLinkPort port, OutputPreset preset, FallbackMode mode, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(source, FfmpegCommandBuilder.BuildFallback(options, port, preset, mode, value));
    }

    public Task StartPortStandbyAsync(SourceIdentity owner, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartProcessAsync(owner, FfmpegCommandBuilder.BuildPortStandby(options, port, preset, configuration));
    }

    public Task StopAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        StopOwnedAsync(source, gracefulStopTimeout, cancellationToken);

    public Task StopForOutputHandoffAsync(SourceIdentity source, CancellationToken cancellationToken) =>
        StopOwnedAsync(source, OutputHandoffStopTimeout, cancellationToken);

    private async Task StopOwnedAsync(SourceIdentity source, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        if (!_running.TryRemove(source.Value, out var managed)) return;
        await StopManagedAsync(managed, stopTimeout, cancellationToken).ConfigureAwait(false);
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
        var processes = _running.ToArray();
        _running.Clear();
        foreach (var pair in processes)
            await StopManagedAsync(pair.Value, gracefulStopTimeout, CancellationToken.None).ConfigureAwait(false);
        _containmentJob.Dispose();
    }

    private Task StartProcessAsync(SourceIdentity source, ProcessStartInfo start)
    {
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var managed = new ManagedProcess(source, process, DateTimeOffset.UtcNow);
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
            return Task.CompletedTask;
        }
        catch
        {
            _running.TryRemove(source.Value, out _);
            process.Dispose();
            throw;
        }
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
            await Task.WhenAll(managed.ProgressTask ?? Task.CompletedTask, managed.ErrorTask ?? Task.CompletedTask).ConfigureAwait(false);
            _last[managed.Source.Value] = CreateSnapshot(managed);
        }
        catch (Exception ex)
        {
            managed.AddError($"Supervisor observation failed: {ex.Message}");
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, ManagedProcess>(managed.Source.Value, managed));
        }
    }

    private static async Task PumpProgressAsync(ManagedProcess managed)
    {
        while (await managed.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var snapshot = managed.Parser.Accept(line, DateTimeOffset.UtcNow);
            if (snapshot is not null) managed.Progress = snapshot;
        }
    }

    private static async Task PumpErrorsAsync(ManagedProcess managed)
    {
        while (await managed.Process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            managed.AddError(LogRedactor.Redact(line));
    }

    private async Task StopManagedAsync(ManagedProcess managed, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        try
        {
            if (!managed.Process.HasExited)
            {
                try
                {
                    await managed.Process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await managed.Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch { }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(stopTimeout);
                try { await managed.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!managed.Process.HasExited) managed.Process.Kill(entireProcessTree: true);
                    await managed.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            _last[managed.Source.Value] = CreateSnapshot(managed);
        }
        finally
        {
            managed.Process.Dispose();
        }
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
        return new(managed.Source, managed.Process.Id, managed.StartedAt, running, managed.Progress, managed.Errors.ToArray(), exitCode);
    }

    private sealed class ManagedProcess(SourceIdentity source, Process process, DateTimeOffset startedAt)
    {
        private const int ErrorLimit = 100;
        public SourceIdentity Source { get; } = source;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public FfmpegProgressParser Parser { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public FfmpegProgressSnapshot? Progress { get; set; }
        public Task? ProgressTask { get; set; }
        public Task? ErrorTask { get; set; }
        public Task? ExitTask { get; set; }

        public void AddError(string line)
        {
            Errors.Enqueue(line);
            while (Errors.Count > ErrorLimit) Errors.TryDequeue(out _);
        }
    }
}
