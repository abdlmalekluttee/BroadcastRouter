using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class AutomaticAssignmentEngine(PortReservationManager reservations, PriorityWaitingQueue waitingQueue)
{
    public AssignmentDecision Assign(DiscoveredSource source, IReadOnlyList<DeckLinkPort> ports, Func<DeckLinkPort, bool>? compatible = null)
    {
        compatible ??= static _ => true;
        var candidates = ports.Where(p => p.IsOutputPort && p.IsAvailable && compatible(p)
            && (!p.Reserved || string.Equals(source.FixedPortId, p.StableId, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (source.FixedPortId is not null)
        {
            var fixedPort = candidates.FirstOrDefault(p => string.Equals(p.StableId, source.FixedPortId, StringComparison.OrdinalIgnoreCase));
            if (fixedPort is not null && reservations.TryReserve(fixedPort.StableId, source.Identity, source.AssignmentLocked, DateTimeOffset.UtcNow, out _))
                return new AssignmentDecision(true, fixedPort, AssignmentMode.Fixed, null);
            waitingQueue.Enqueue(source.Identity, source.Priority, "Fixed output is unavailable or incompatible.");
            return new AssignmentDecision(false, null, AssignmentMode.Fixed, "Fixed output is unavailable or incompatible.");
        }

        if (!source.AutomaticRoutingEnabled)
            return new AssignmentDecision(false, null, AssignmentMode.None, "Automatic routing is disabled.");

        foreach (var port in candidates.OrderBy(p => p.CardIndex).ThenBy(p => p.SubdeviceIndex))
            if (reservations.TryReserve(port.StableId, source.Identity, false, DateTimeOffset.UtcNow, out _))
                return new AssignmentDecision(true, port, AssignmentMode.Automatic, null);

        waitingQueue.Enqueue(source.Identity, source.Priority, "No compatible DeckLink output is available.");
        return new AssignmentDecision(false, null, AssignmentMode.Automatic, "No compatible DeckLink output is available.");
    }
}

public sealed record AssignmentDecision(bool Assigned, DeckLinkPort? Port, AssignmentMode Mode, string? Reason);
