namespace BroadcastRouter.Application;

public static class RetryLimitPolicy
{
    public static bool IsExhausted(int attemptedRetryCount, int maximumRetryAttempts) =>
        maximumRetryAttempts > 0 && attemptedRetryCount > maximumRetryAttempts;
}
