using BroadcastRouter.Domain;
using System.Collections.Concurrent;

namespace BroadcastRouter.Application;

public enum PortReleaseResult
{
    Released,
    AlreadyFree,
    OwnedByOther,
    Locked
}

public sealed class PortReservationManager
{
    private readonly ConcurrentDictionary<string, OutputController> _controllers = new(StringComparer.OrdinalIgnoreCase);

    public OutputController ControllerFor(string portId)
    {
        if (string.IsNullOrWhiteSpace(portId)) throw new ArgumentException("Port ID is required.", nameof(portId));
        return _controllers.GetOrAdd(portId, static id => new(id));
    }

    public IReadOnlyList<OutputController> Controllers => _controllers.Values.ToArray();

    public bool TryReserve(string portId, SourceIdentity source, bool locked, DateTimeOffset now, out PortReservation reservation)
    {
        return ControllerFor(portId).TryReserve(source, locked, now, out reservation);
    }

    public bool Release(string portId, SourceIdentity source, bool force = false)
        => ReleaseWithResult(portId, source, force) == PortReleaseResult.Released;

    public PortReleaseResult ReleaseWithResult(string portId, SourceIdentity source, bool force = false)
    {
        return ControllerFor(portId).Release(source, force);
    }

    public bool IsAvailable(string portId)
    {
        return ControllerFor(portId).Reservation is null;
    }

    public IReadOnlyList<PortReservation> Snapshot()
    {
        return _controllers.Values.Select(controller => controller.Reservation).OfType<PortReservation>()
            .OrderBy(x => x.PortId, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
