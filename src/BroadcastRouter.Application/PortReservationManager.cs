using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class PortReservationManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PortReservation> _byPort = new(StringComparer.OrdinalIgnoreCase);

    public bool TryReserve(string portId, SourceIdentity source, bool locked, DateTimeOffset now, out PortReservation reservation)
    {
        if (string.IsNullOrWhiteSpace(portId)) throw new ArgumentException("Port ID is required.", nameof(portId));
        lock (_gate)
        {
            if (_byPort.TryGetValue(portId, out var existing))
            {
                if (existing.Source != source)
                {
                    reservation = existing;
                    return false;
                }

                reservation = existing with { Locked = existing.Locked || locked };
                _byPort[portId] = reservation;
                return true;
            }

            reservation = new PortReservation(portId, source, locked, now);
            _byPort.Add(portId, reservation);
            return true;
        }
    }

    public bool Release(string portId, SourceIdentity source, bool force = false)
    {
        lock (_gate)
        {
            if (!_byPort.TryGetValue(portId, out var existing) || existing.Source != source) return false;
            if (existing.Locked && !force) return false;
            return _byPort.Remove(portId);
        }
    }

    public bool IsAvailable(string portId)
    {
        lock (_gate) return !_byPort.ContainsKey(portId);
    }

    public IReadOnlyList<PortReservation> Snapshot()
    {
        lock (_gate) return _byPort.Values.OrderBy(x => x.PortId, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
