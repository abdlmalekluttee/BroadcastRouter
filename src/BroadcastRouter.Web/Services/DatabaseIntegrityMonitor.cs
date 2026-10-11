using BroadcastRouter.Infrastructure;
using BroadcastRouter.Application;

namespace BroadcastRouter.Web.Services;

public sealed record DatabaseIntegritySnapshot(string Result, DateTimeOffset CheckedAt)
{
    public bool IsHealthy => Result.Equals("ok", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Runs the comparatively expensive SQLite integrity check away from HTTP
/// requests. The anonymous health endpoint reads this immutable snapshot, so
/// aggressive monitoring cannot create database contention or false timeouts.
/// </summary>
public sealed class DatabaseIntegrityMonitor(
    SqliteDataStore store,
    ILogger<DatabaseIntegrityMonitor> logger, SupervisionClock? clock = null) : BackgroundService
{
    private readonly SupervisionClock _clock = clock ?? SupervisionClock.System;
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private DatabaseIntegritySnapshot _snapshot = new("unknown", DateTimeOffset.MinValue);

    public DatabaseIntegritySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await store.IntegrityCheckAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, new(result, _clock.UtcNow));
            _lastChecked = _clock.Now;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _snapshot, new("error", _clock.UtcNow));
            _lastChecked = _clock.Now;
            logger.LogWarning(exception, "The background database integrity check failed.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var age = _clock.Now - _lastChecked;
            if (age >= RefreshInterval)
                await RefreshAsync(stoppingToken).ConfigureAwait(false);

            await _clock.DelayAsync(RefreshInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
