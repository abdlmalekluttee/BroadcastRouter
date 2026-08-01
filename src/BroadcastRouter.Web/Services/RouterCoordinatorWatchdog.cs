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
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private int _recoveryStarted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var liveness = coordinator.GetLiveness();
            if (CoordinatorLivenessPolicy.IsResponsive(liveness, now, MaximumSilence)) continue;
            if (Interlocked.Exchange(ref _recoveryStarted, 1) != 0) return;

            var silence = now - liveness.LastProgressAt;
            var message = $"Coordinator made no progress for {silence.TotalSeconds:F0} seconds "
                + $"while in stage '{liveness.Stage}'. The host will terminate so Windows Service recovery can restore routing.";
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
}
