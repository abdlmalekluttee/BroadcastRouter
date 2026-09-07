namespace BroadcastRouter.Application;

public sealed class ProcessCpuSampler(TimeSpan minimumWindow)
{
    private readonly object _gate = new();
    private TimeSpan? _lastCpuTime;
    private DateTimeOffset? _lastObservedAt;
    private double _lastPercent;

    public double Sample(TimeSpan cpuTime, DateTimeOffset observedAt, int processorCount)
    {
        if (processorCount <= 0) throw new ArgumentOutOfRangeException(nameof(processorCount));

        lock (_gate)
        {
            if (_lastCpuTime is null || _lastObservedAt is null)
            {
                _lastCpuTime = cpuTime;
                _lastObservedAt = observedAt;
                return _lastPercent;
            }

            var elapsed = observedAt - _lastObservedAt.Value;
            if (elapsed < minimumWindow) return _lastPercent;

            var consumed = cpuTime - _lastCpuTime.Value;
            _lastPercent = Math.Clamp(
                consumed.TotalSeconds / (elapsed.TotalSeconds * processorCount) * 100,
                0,
                100);
            _lastCpuTime = cpuTime;
            _lastObservedAt = observedAt;
            return _lastPercent;
        }
    }
}
