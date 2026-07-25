using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed class PriorityWaitingQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<SourceIdentity, QueueItem> _items = [];
    private long _sequence;

    public void Enqueue(SourceIdentity source, int priority, string reason)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(source, out var existing))
                _items[source] = existing with { Priority = priority, Reason = reason };
            else
                _items[source] = new QueueItem(source, priority, reason, _sequence++);
        }
    }

    public QueueItem? Dequeue()
    {
        lock (_gate)
        {
            var next = _items.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.Sequence).FirstOrDefault();
            if (next is not null) _items.Remove(next.Source);
            return next;
        }
    }

    public bool Remove(SourceIdentity source)
    {
        lock (_gate) return _items.Remove(source);
    }

    public IReadOnlyList<QueueItem> Snapshot()
    {
        lock (_gate) return _items.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.Sequence).ToArray();
    }
}

public sealed record QueueItem(SourceIdentity Source, int Priority, string Reason, long Sequence);
