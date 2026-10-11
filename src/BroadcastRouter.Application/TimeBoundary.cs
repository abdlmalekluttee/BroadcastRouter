using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

/// <summary>
/// A single sampled wall/elapsed boundary. Public status represents elapsed ages
/// and remaining deadlines in the current UTC frame. It is not an historical event
/// clock: durable audit events must capture UtcNow at the event itself. UTC storage
/// is rebased on load, so run-local timeline coordinates never survive a restart.
/// </summary>
public readonly record struct TimeBoundary(DateTimeOffset MonotonicNow, DateTimeOffset UtcNow)
{
    public DateTimeOffset ToUtc(DateTimeOffset value) => value == DateTimeOffset.MinValue ? value : UtcNow + (value - MonotonicNow);
    public DateTimeOffset? ToUtc(DateTimeOffset? value) => value is null ? null : ToUtc(value.Value);
    public DateTimeOffset FromUtc(DateTimeOffset value) => value == DateTimeOffset.MinValue ? value : MonotonicNow + (value - UtcNow);
    public DateTimeOffset? FromUtc(DateTimeOffset? value) => value is null ? null : FromUtc(value.Value);
    public RuntimeRoute RouteToUtc(RuntimeRoute route) => route with
    {
        StartedAt = ToUtc(route.StartedAt), UpdatedAt = ToUtc(route.UpdatedAt), RetryAt = ToUtc(route.RetryAt)
    };
    public RuntimeRoute RouteFromUtc(RuntimeRoute route) => route with
    {
        StartedAt = FromUtc(route.StartedAt), UpdatedAt = FromUtc(route.UpdatedAt), RetryAt = FromUtc(route.RetryAt)
    };
    public DiscoveredSource SourceToUtc(DiscoveredSource source) => source with { LastObservedAt = ToUtc(source.LastObservedAt) };
    public DiscoveredSource SourceFromUtc(DiscoveredSource source) => source with { LastObservedAt = FromUtc(source.LastObservedAt) };
}
