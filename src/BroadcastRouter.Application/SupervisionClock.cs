namespace BroadcastRouter.Application;

/// <summary>
/// One service-run clock shared by all supervision consumers. Now is a UTC-anchored
/// monotonic timeline adapter for existing DateTimeOffset policy signatures: its
/// intervals come only from TimeProvider timestamps. UtcNow is the wall clock for
/// audit records. Do not construct separate clocks for components of one run.
/// Values on Now are internal only. Convert elapsed/deadline DTOs at every UTC
/// boundary, and record audit events directly with UtcNow.
/// </summary>
public sealed class SupervisionClock(TimeProvider provider)
{
    public static SupervisionClock System { get; } = new(TimeProvider.System);
    private readonly DateTimeOffset _originUtc = provider.GetUtcNow();
    private readonly long _origin = provider.GetTimestamp();

    public DateTimeOffset Now => _originUtc + provider.GetElapsedTime(_origin);
    public DateTimeOffset UtcNow => provider.GetUtcNow();
    public TimeBoundary CaptureBoundary() => new(Now, UtcNow);
    public PeriodicTimer CreatePeriodicTimer(TimeSpan interval) => new(interval, provider);
    public long GetTimestamp() => provider.GetTimestamp();
    public TimeSpan GetElapsedTime(long start) => provider.GetElapsedTime(start);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, provider, cancellationToken);
}
