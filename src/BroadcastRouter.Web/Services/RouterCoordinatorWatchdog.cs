using BroadcastRouter.Application;
using BroadcastRouter.Infrastructure;

namespace BroadcastRouter.Web.Services;

/// <summary>
/// The HTTP host can remain responsive when the routing worker is blocked in a
/// native media operation. Fail fast only after a sustained coordinator stall
/// so the Windows Service Control Manager can restart the process and the Job
/// Object can remove the exact owned media children.
/// </summary>
public sealed class RouterCoordinatorWatchdog(
    RouterCoordinator coordinator,
    SqliteDataStore store,
    ILogger<RouterCoordinatorWatchdog> logger,
    SupervisionClock? clock = null,
    SupervisionTelemetry? telemetry = null) : BackgroundService
{
    private readonly SupervisionClock _clock = clock ?? SupervisionClock.System;
    internal static readonly TimeSpan MaximumSilence = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumFastInputSilence = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumFastPublisherSilence = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private int _recoveryStarted;
    internal Action<string> TerminateProcess { get; init; } = Environment.FailFast;
    internal TimeSpan LogBudget { get; init; } = TimeSpan.FromSeconds(5);
    internal Func<CancellationToken, Task>? PersistRecoveryRecordForTesting { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _clock.DelayAsync(CheckInterval, stoppingToken).ConfigureAwait(false);
            var now = _clock.Now;
            var liveness = coordinator.GetLiveness();
            var coordinatorResponsive = CoordinatorLivenessPolicy.IsResponsive(liveness, now, MaximumSilence);
            var stalledAuxiliary = coordinator.GetAuxiliaryLiveness().FirstOrDefault(snapshot =>
                !AuxiliaryLoopLivenessPolicy.IsResponsive(snapshot, now, MaximumSilenceFor(snapshot.Name)));
            if (coordinatorResponsive && stalledAuxiliary is null) continue;
            if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0) return;

            string message;
            if (!coordinatorResponsive)
            {
                var silence = now - liveness.LastProgressAt;
                message = $"Coordinator made no progress for {silence.TotalSeconds:F0} seconds "
                    + $"while in stage '{liveness.Stage}'. The host will terminate so Windows Service recovery can restore routing.";
            }
            else
            {
                var silence = now - stalledAuxiliary!.LastProgressAt;
                message = $"Critical auxiliary loop '{stalledAuxiliary.Name}' made no progress for "
                    + $"{silence.TotalSeconds:F0} seconds after {stalledAuxiliary.RestartCount} restart attempt(s). "
                    + "The host will terminate so Windows Service recovery can restore routing.";
            }
            await TerminateForStallAsync(message, coordinatorResponsive ? "AuxiliaryLoopStall" : "CoordinatorStall",
                new(ProgressAgeMilliseconds: (now - (coordinatorResponsive ? stalledAuxiliary!.LastProgressAt : liveness.LastProgressAt)).TotalMilliseconds),
                coordinatorResponsive ? MaximumSilenceFor(stalledAuxiliary!.Name) : MaximumSilence, stoppingToken);
            return;
        }
    }

    internal async Task TerminateForStallAsync(string message, string trigger, SupervisionEvidence evidence,
        TimeSpan window, CancellationToken stoppingToken)
    {
        logger.LogCritical("{Category}: {Message}", "CoordinatorWatchdog", message);
        telemetry?.Record("HOST", null, null, trigger, evidence, null,
            "Existing sustained coordinator/auxiliary silence policy", window.TotalMilliseconds,
            ["Wait", "Route-only recovery (not implemented until P2-30)"], "HostFailFast", "TerminationRequestedNotConfirmed");
        foreach (var process in coordinator.OwnedProcessesForDiagnostics())
            telemetry?.Record(process.Source.Value, null, process, trigger,
                SupervisionTelemetry.Evidence(process, _clock), null, "Host termination affects all exact owned sessions",
                window.TotalMilliseconds, ["Leave child running"], "HostFailFastChildImpact", "TerminationRequestedNotConfirmed");

        using var logDeadline = new CancellationTokenSource(LogBudget);
        if (telemetry is not null)
        {
            try { await telemetry.FlushAsync(logDeadline.Token).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogError(ex, "Watchdog telemetry drain failed; termination evidence may be incomplete."); }
        }
        try
        {
            // SQLite's async API may block before returning its Task. Isolate the
            // best-effort invocation itself, not only the wait on its returned Task.
            var logCancellation = logDeadline.Token;
            var write = Task.Run(() => PersistRecoveryRecordForTesting is { } persist ? persist(logCancellation)
                : store.WriteLogAsync("Critical", "CoordinatorWatchdog", message, cancellationToken: logCancellation), logCancellation);
            _ = write.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await write.WaitAsync(logDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        { logger.LogError(ex, "Coordinator watchdog could not persist its recovery record."); }

        // Keep the existing destructive policy unchanged in Phase A. No claim that it is safe:
        // P2-30 replaces whole-host recovery. The seam prevents tests from terminating a host.
        TerminateProcess(message);
    }

    internal static TimeSpan MaximumSilenceFor(string loopName) =>
        loopName.Equals("FastInputSupervision", StringComparison.Ordinal)
            ? MaximumFastInputSilence
            : MaximumFastPublisherSilence;
}
