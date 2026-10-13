using BroadcastRouter.Infrastructure;

namespace BroadcastRouter.Web.Services;

// Registered before lifecycle/coordinator services: their stop events enter the
// queue first, then this final bounded drain closes diagnostic admission.
public sealed class DiagnosticPersistenceWorker(SqliteDataStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => store.StopDiagnosticsAsync().AsTask();
}
