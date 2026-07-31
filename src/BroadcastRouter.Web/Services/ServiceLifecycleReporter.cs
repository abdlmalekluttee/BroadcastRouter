using System.Diagnostics;
using System.Reflection;
using BroadcastRouter.Infrastructure;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace BroadcastRouter.Web.Services;

/// <summary>
/// Persists orderly host start/stop boundaries. Unexpected termination is
/// recorded by the Windows Service Control Manager; the missing matching stop
/// record makes that distinction visible in application diagnostics as well.
/// </summary>
public sealed class ServiceLifecycleReporter(
    SqliteDataStore store,
    ILogger<ServiceLifecycleReporter> logger) : IHostedService
{
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = WindowsServiceHelpers.IsWindowsService() ? "Windows Service" : "interactive process";
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        using var process = Process.GetCurrentProcess();
        var message = $"BroadcastRouter {version} started as {mode}; PID {process.Id}, session {process.SessionId}, run {_runId}.";
        logger.LogInformation("{Category}: {Message}", "ServiceLifecycle", message);
        await store.WriteLogAsync("Information", "ServiceLifecycle", message,
            correlationId: _runId, cancellationToken: cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var message = $"BroadcastRouter run {_runId} completed orderly shutdown after owned media processes were stopped.";
        logger.LogInformation("{Category}: {Message}", "ServiceLifecycle", message);
        await store.WriteLogAsync("Information", "ServiceLifecycle", message,
            correlationId: _runId, cancellationToken: CancellationToken.None);
    }
}
