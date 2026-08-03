namespace BroadcastRouter.Application;

public static class ChangeNotificationDispatcher
{
    public static IReadOnlyList<Exception> Dispatch(Action? handlers)
    {
        if (handlers is null) return [];
        var failures = new List<Exception>();
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        return failures;
    }
}
