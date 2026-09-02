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
    ILogger<RouterCoordinatorWatchdog> logger) : BackgroundService
{
    internal static readonly TimeSpan MaximumSilence = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumFastInputSilence = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumFastPublisherSilence = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private int _recoveryStarted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
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
            logger.LogCritical("{Category}: {Message}", "CoordinatorWatchdog", message);

            using var logDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await store.WriteLogAsync("Critical", "CoordinatorWatchdog", message,
                    cancellationToken: logDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Coordinator watchdog could not persist its recovery record.");
            }

            Environment.FailFast(message);
        }
    }

    internal static TimeSpan MaximumSilenceFor(string loopName) =>
        loopName.Equals("FastInputSupervision", StringComparison.Ordinal)
            ? MaximumFastInputSilence
            : MaximumFastPublisherSilence;
}
