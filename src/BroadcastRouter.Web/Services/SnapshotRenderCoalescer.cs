namespace BroadcastRouter.Web.Services;

public sealed class SnapshotRenderCoalescer
{
    private int _pending;

    public async Task DispatchAsync(Func<Action, Task> dispatcher, Action render)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;

        var callbackStarted = false;
        try
        {
            await dispatcher(() =>
            {
                callbackStarted = true;
                Interlocked.Exchange(ref _pending, 0);
                render();
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!callbackStarted) Interlocked.Exchange(ref _pending, 0);
        }
    }
}
