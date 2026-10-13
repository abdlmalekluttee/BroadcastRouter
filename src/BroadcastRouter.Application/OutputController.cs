using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record OutputProcessSession(SourceIdentity Source, string Purpose, Guid SessionId,
    int ProcessId, long ConfigurationGeneration);

public sealed record OutputControllerStatus(string PortId, PortReservation? Reservation,
    OutputProcessSession? Session, bool OperationActive, string? Alarm, long ControllerEpoch);

/// <summary>Physical-output ownership, independent of a source's changing identity.</summary>
public sealed class OutputController(string portId)
{
    private readonly object _state = new();
    // Lifetime belongs to the registry, not to a process/configuration generation.
    // No wait handle is requested; GC reclaims the gate after its last user.
    private readonly SemaphoreSlim _execution = new(1, 1);
    private PortReservation? _reservation;
    private OutputProcessSession? _session;
    private bool _operationActive;
    private Guid _operationId;
    private string? _alarm;
    private long _controllerEpoch = 1;
    private static readonly TimeSpan AdmissionBudget = TimeSpan.FromSeconds(1);
    // Covers maximum configured graceful stop (30s), forced reap (5s), pipe
    // drain (5s) and signalling. Not a media-health threshold.
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(45);
    public string PortId { get; } = portId;
    public PortReservation? Reservation { get { lock (_state) return _reservation; } }
    public OutputProcessSession? Session { get { lock (_state) return _session; } }

    public OutputControllerStatus Status
    {
        get
        {
            lock (_state)
                return new(PortId, _reservation, _session, _operationActive, _alarm, _controllerEpoch);
        }
    }

    public Task ExecuteAsync(Func<Task> intent, CancellationToken cancellationToken) =>
        ExecuteAsync(_ => intent(), cancellationToken);

    public async Task ExecuteAsync(Func<CancellationToken, Task> intent, CancellationToken cancellationToken)
    {
        if (!await _execution.WaitAsync(AdmissionBudget, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("The physical output controller is busy; ownership was not changed.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationBudget);
        var token = deadline.Token;
        var operationId = Guid.NewGuid();
        lock (_state) { _operationActive = true; _operationId = operationId; }
        // Isolate a dependency that blocks synchronously before returning its
        // Task. Only this worker releases the execution gate, never its caller.
        var work = Task.Run(async () =>
        {
            try { token.ThrowIfCancellationRequested(); await intent(token).ConfigureAwait(false); }
            finally
            {
                lock (_state) _operationActive = false;
                _execution.Release();
                deadline.Dispose();
            }
        }, CancellationToken.None);
        _ = work.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await work.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_state) if (_operationActive && _operationId == operationId)
                    _alarm = "Output operation did not finish before cancellation; ownership retained.";
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Output operation exceeded its bounded deadline; ownership retained.");
        }
    }

    public bool TryRecoverIdle()
    {
        // Never replace a semaphore underneath a worker or discard a session
        // whose native output may still be owned. Reservations survive recovery.
        if (!_execution.Wait(0)) return false;
        try
        {
            lock (_state)
            {
                if (_operationActive || _session is not null || _alarm is null) return false;
                _alarm = null;
                _controllerEpoch++;
                return true;
            }
        }
        finally { _execution.Release(); }
    }

    public void Alarm(string reason) { lock (_state) _alarm = reason; }

    public void Started(OutputProcessSession session)
    {
        lock (_state)
        {
            if (_session is not null) throw new InvalidOperationException("The physical output already has an owned session.");
            _session = session;
        }
    }

    public void Exited(Guid sessionId)
    {
        lock (_state) if (_session?.SessionId == sessionId) _session = null;
    }

    internal bool TryReserve(SourceIdentity source, bool locked, DateTimeOffset now, out PortReservation reservation)
    {
        lock (_state)
        {
            if (_reservation is { } existing)
            {
                if (existing.Source != source) { reservation = existing; return false; }
                reservation = _reservation = existing with { Locked = existing.Locked || locked };
                return true;
            }
            reservation = _reservation = new(PortId, source, locked, now);
            return true;
        }
    }

    internal PortReleaseResult Release(SourceIdentity source, bool force)
    {
        lock (_state)
        {
            if (_reservation is null) return PortReleaseResult.AlreadyFree;
            if (_reservation.Source != source) return PortReleaseResult.OwnedByOther;
            if (_reservation.Locked && !force) return PortReleaseResult.Locked;
            _reservation = null;
            return PortReleaseResult.Released;
        }
    }
}
