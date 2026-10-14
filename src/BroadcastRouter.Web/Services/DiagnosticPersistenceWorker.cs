using BroadcastRouter.Infrastructure;
using BroadcastRouter.Application;

namespace BroadcastRouter.Web.Services;

// Registered before lifecycle/coordinator services: their stop events enter the
// queue first, then this final bounded drain closes diagnostic admission.
public sealed class DiagnosticPersistenceWorker(SqliteDataStore store, SupervisionClock? clock = null) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        var timer = clock ?? SupervisionClock.System;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await store.PruneDiagnosticLogsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { store.ReportDiagnosticMaintenanceFailure(); }
            try { await timer.DelayAsync(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }, stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken); }
        finally { await store.StopDiagnosticsAsync(); }
    }
}
