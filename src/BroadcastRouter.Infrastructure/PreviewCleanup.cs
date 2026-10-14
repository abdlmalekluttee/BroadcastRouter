namespace BroadcastRouter.Infrastructure;

/// <summary>Preview teardown must release its control gate even if reap or drainage fails.
/// Failure is explicit: disposing ownership is not proof of a confirmed clean exit.</summary>
internal static class PreviewCleanup
{
    internal static async Task<string?> RunAsync(Func<CancellationToken, Task> stopProcess,
        Task? drain, Action dispose)
    {
        var errors = new List<string>();
        try
        {
            using var reapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await stopProcess(reapDeadline.Token).WaitAsync(reapDeadline.Token).ConfigureAwait(false);
            }
            catch { errors.Add("Preview process exit could not be confirmed within 5 seconds."); }
            if (drain is not null)
            {
                try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { errors.Add("Preview stderr drain did not complete successfully within 2 seconds."); }
            }
        }
        finally
        {
            try { dispose(); }
            catch { errors.Add("Preview resources could not be fully released."); }
        }
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }
}
