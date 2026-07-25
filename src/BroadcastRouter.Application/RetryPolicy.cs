namespace BroadcastRouter.Application;

public sealed class RetryPolicy
{
    private readonly TimeSpan[] _delays;

    public RetryPolicy(params TimeSpan[] delays)
    {
        if (delays.Length == 0) throw new ArgumentException("At least one retry delay is required.", nameof(delays));
        if (delays.Any(delay => delay < TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(delays), "Retry delays cannot be negative.");
        _delays = [.. delays];
    }

    public static RetryPolicy BroadcastDefault { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30));

    public TimeSpan GetDelay(int failureCount)
    {
        if (failureCount < 1) throw new ArgumentOutOfRangeException(nameof(failureCount));
        return _delays[Math.Min(failureCount - 1, _delays.Length - 1)];
    }
}
