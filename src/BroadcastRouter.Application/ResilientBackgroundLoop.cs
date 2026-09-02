namespace BroadcastRouter.Application;

/// <summary>
/// Keeps a critical auxiliary loop alive when an unexpected exception escapes
/// its per-item isolation. Cancellation remains authoritative; failure
/// reporting is deliberately best-effort so a logging outage cannot disable
/// the recovery loop itself.
/// </summary>
public static class ResilientBackgroundLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> loop,
        Func<Exception, CancellationToken, Task> reportFailure,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await loop(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;
                throw new InvalidOperationException("A critical background loop returned unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                try
                {
                    await reportFailure(ex, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Failure reporting must never become a second single point
                    // of failure for the loop being protected.
                }

                if (retryDelay > TimeSpan.Zero)
                {
                    try { await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                }
            }
        }
    }
}
