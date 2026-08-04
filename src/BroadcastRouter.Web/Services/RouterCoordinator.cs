using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace BroadcastRouter.Web.Services;

public sealed class RouterCoordinator(
    SqliteDataStore store,
    IHttpClientFactory httpClientFactory,
    IHubContext<StatusHub> hub,
    ILogger<RouterCoordinator> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly PortReservationManager _reservations = new();
    private readonly PriorityWaitingQueue _waiting = new();
    private readonly RouteStateMachine _stateMachine = new();
    private readonly Dictionary<string, DiscoveredSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeckLinkPort> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RuntimeRoute> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerHealth> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _sourceMissingSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _sourceReadySince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _simulationFaults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routeGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly Dictionary<string, PortStandbyStatus> _standbys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _standbyRetryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _standbyConfigurationSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _processDiagnosticSignatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FfmpegInputFreezeDetector> _inputFreezeDetectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceMediaModeState> _sourceMediaModes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastExtendedVideoProbeAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingMediaModeRestarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _cutoverStartedAt = new(StringComparer.Ordinal);
    private readonly StartupRouteRecoveryTracker _startupRecovery = new();
    private readonly RepeatedFailureLogGate _reconciliationFailureLogGate = new();
    private readonly PublisherDisconnectDetector _publisherDisconnectDetector = new(requiredObservations: 2);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _livenessGate = new();
    private readonly object _metricsGate = new();
    private DateTimeOffset _coordinatorProgressAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastCompletedCycleAt;
    private string _coordinatorStage = "Startup";
    private long _completedCycles;
    private FfmpegProcessSupervisor? _supervisor;
    private string? _supervisorPath;
    private bool _supervisorUsesWindowsDeckLinkSafeTerminate;
    private int _supervisorInputReadTimeoutMilliseconds;
    private OperatorSettings _settings = new();
    private MediaToolValidation _validation = MediaToolValidation.NotConfigured;
    private volatile bool _emergencyStopped;
    private int _forceDiscoveryRequested = 1;
    private volatile bool _forceToolValidation = true;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastToolValidation = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDeckLinkReferenceStatusCheck = DateTimeOffset.MinValue;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.UtcNow;
    private static readonly TimeSpan ExtendedVideoProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExtendedVideoProbeDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ExtendedVideoProbeTimeout = TimeSpan.FromSeconds(16);

    public event Action? Changed;

    public RouterSnapshot Snapshot { get; private set; } = new([], [], [], [], [], MediaToolValidation.NotConfigured,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, true, false, "Starting");

    public OperatorSettings GetSettings()
    {
        OperatorSettings settings;
        lock (_gate) settings = _settings;
        return Clone(settings);
    }

    public long SettingsRevision { get { lock (_gate) return _settings.ConfigurationRevision; } }
    public bool RequiresAuthentication { get { lock (_gate) return _settings.Security.RequireAuthentication; } }

    public CoordinatorLivenessSnapshot GetLiveness()
    {
        lock (_livenessGate)
            return new(_startedAt, _coordinatorProgressAt, _lastCompletedCycleAt,
                _coordinatorStage, _completedCycles);
    }

    public async Task<SettingsApplyResult> SaveSettingsAsync(OperatorSettings settings, string actor = "system",
        CancellationToken cancellationToken = default)
    {
        var submitted = Clone(settings);
        await _settingsMutationGate.WaitAsync(cancellationToken);
        try
        {
            OperatorSettings current;
            RuntimeRoute[] dependentRoutes;
            DeckLinkPort[] currentPorts;
            lock (_gate)
            {
                current = Clone(_settings);
                dependentRoutes = _routes.Values.Where(route => route.State is not RouteState.Released and not RouteState.Disabled).ToArray();
                currentPorts = _ports.Values.ToArray();
            }
            SettingsConcurrencyPolicy.EnsureCurrent(submitted.ConfigurationRevision, current.ConfigurationRevision);
            foreach (var rule in submitted.Rules)
            {
                RoutingRuleEvaluator.ValidatePattern(rule.ServerPattern);
                RoutingRuleEvaluator.ValidatePattern(rule.ApplicationPattern);
                RoutingRuleEvaluator.ValidatePattern(rule.InstancePattern);
                RoutingRuleEvaluator.ValidatePattern(rule.StreamPattern);
            }
            OutputPresetSelection.EnsureReferencesAvailable(submitted.Presets, dependentRoutes.Select(route => route.PresetId));
            var requestedOutputs = submitted.DeckLinkPortOverrides.Where(value => value.IsOutputPort)
                .Select(value => value.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activePorts = dependentRoutes.Where(route => route.PortId is not null)
                .Select(route => route.PortId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (activePorts.Any(portId => !requestedOutputs.Contains(portId)))
                throw new InvalidOperationException("Stop routes on a connector before removing its output-port designation.");

            var appliedAt = DateTimeOffset.UtcNow;
            SettingsConcurrencyPolicy.MarkApplied(submitted, current.ConfigurationRevision, appliedAt, actor);
            await store.SaveSettingsAsync(submitted, cancellationToken);
            var requiresToolValidation = current.SimulationMode != submitted.SimulationMode
                || !string.Equals(current.MediaTools.FfmpegPath, submitted.MediaTools.FfmpegPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.MediaTools.FfprobePath, submitted.MediaTools.FfprobePath, StringComparison.OrdinalIgnoreCase);
            var appliedPorts = ApplyOverrides(currentPorts, submitted.DeckLinkCardOverrides, submitted.DeckLinkPortOverrides);
            lock (_gate)
            {
                _settings = Clone(submitted);
                _ports.Clear();
                foreach (var port in appliedPorts) _ports[port.StableId] = port;
                Interlocked.Exchange(ref _forceDiscoveryRequested, 1);
                _forceToolValidation |= requiresToolValidation;
            }
            await AuditPortConfigurationChangesAsync(current, submitted, currentPorts, actor,
                "Operator settings save", "Persisted and applied", cancellationToken);
            await LogAsync("Information", "Settings",
                $"Configuration revision {submitted.ConfigurationRevision} was saved and applied immediately by {actor} at {appliedAt:O}.",
                cancellationToken: cancellationToken);
            if (!requiresToolValidation)
                await ReconcilePortStandbysAsync(cancellationToken);
            Publish($"Configuration {submitted.ConfigurationRevision} applied");
            return new(Clone(submitted), submitted.ConfigurationRevision, appliedAt, submitted.LastAppliedBy);
        }
        finally { _settingsMutationGate.Release(); }
    }

    public async Task SetWowzaPasswordAsync(WowzaServerProfile server, string plaintext, CancellationToken cancellationToken = default)
    {
        server.ProtectedPassword = string.IsNullOrEmpty(plaintext) ? "" : WindowsDpapi.Protect(plaintext);
        await Task.CompletedTask;
    }

    public async Task<WowzaConnectionTestResult> TestWowzaAsync(WowzaServerProfile profile, string? plaintextPassword = null, CancellationToken cancellationToken = default)
    {
        var password = plaintextPassword;
        if (password is null && !string.IsNullOrWhiteSpace(profile.ProtectedPassword))
        {
            try { password = WindowsDpapi.Unprotect(profile.ProtectedPassword); }
            catch { password = ""; }
        }
        var result = await WowzaConnectionTester.TestAsync(profile, password ?? "", cancellationToken);
        await LogAsync(result.Authenticated ? "Information" : "Warning", "WowzaTest", $"{profile.ServerId}: {result.Summary}", cancellationToken: cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<StructuredLogEntry>> ReadLogsAsync(string? search, int limit = 500, CancellationToken cancellationToken = default) =>
        await store.ReadLogsAsync(search, limit, cancellationToken);

    public async Task<IReadOnlyList<ConfigurationAuditEntry>> ReadConfigurationAuditAsync(int limit = 1000,
        CancellationToken cancellationToken = default) => await store.ReadConfigurationAuditAsync(limit, cancellationToken);

    internal async Task CommandAsync(string action, string? sourceId = null, string? portId = null, string? presetId = null,
        AssignmentMode requestedMode = AssignmentMode.Manual, bool reserveWhileOffline = true, bool allowTemporaryUse = false,
        string actor = "system", CancellationToken cancellationToken = default)
    {
        action = action.Trim().ToLowerInvariant();
        if (action == "emergency-stop")
        {
            _emergencyStopped = true;
            var failures = new List<string>();
            foreach (var activeRoute in RoutesCopy())
            {
                try { await StopRouteAsync(activeRoute.SourceId, forceRelease: true, CancellationToken.None); }
                catch (Exception ex) { failures.Add($"{activeRoute.SourceId}: {LogRedactor.Redact(ex.Message)}"); }
            }
            try { await StopAllPortStandbysAsync(CancellationToken.None); }
            catch (Exception ex) { failures.Add($"standby: {LogRedactor.Redact(ex.Message)}"); }
            var message = failures.Count == 0
                ? "Emergency stop activated. All owned FFmpeg processes were stopped."
                : $"Emergency stop activated, but {failures.Count} owned route(s) reported a stop failure. Review diagnostics immediately.";
            await LogAsync(failures.Count == 0 ? "Critical" : "Error", "Operator", message, correlationId: NewCorrelation(), cancellationToken: CancellationToken.None);
            Publish("Emergency stop active");
            if (failures.Count > 0) throw new InvalidOperationException(message);
            return;
        }
        if (action == "clear-emergency")
        {
            _emergencyStopped = false;
            await LogAsync("Warning", "Operator", "Emergency stop cleared; automatic routing may resume.", cancellationToken: cancellationToken);
            Publish("Emergency stop cleared");
            return;
        }
        if (action == "rescan")
        {
            _forceToolValidation = true;
            Interlocked.Exchange(ref _forceDiscoveryRequested, 1);
            await LogAsync("Information", "Operator", "Hardware and source rescan requested.", cancellationToken: cancellationToken);
            Publish("Rescan requested");
            return;
        }
        if (action == "refresh-sources")
        {
            var correlation = NewCorrelation();
            await LogAsync("Information", "Operator", "Immediate source discovery requested.", correlationId: correlation,
                cancellationToken: cancellationToken);
            var result = await DiscoverAndProbeAsync(cancellationToken, force: true);
            if (result is null) throw new InvalidOperationException("Source discovery did not run.");
            var message = $"Source discovery completed in {result.Elapsed.TotalMilliseconds:F0} ms: "
                + $"{result.ObservedSources} stream(s), {result.SuccessfulServers}/{result.EnabledServers} server(s) reachable.";
            await LogAsync(result.EnabledServers > 0 && result.SuccessfulServers == 0 ? "Warning" : "Information",
                "Discovery", message, correlationId: correlation, cancellationToken: cancellationToken);
            Publish("Source discovery completed");
            if (result.EnabledServers > 0 && result.SuccessfulServers == 0)
                throw new InvalidOperationException("Source discovery completed, but no enabled Wowza server was reachable. Existing routes were retained.");
            return;
        }
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source ID is required for this action.", nameof(sourceId));

        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        switch (action)
        {
            case "start":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                await SaveDesiredRouteAsync(sourceId, portId, presetId, requestedMode, reserveWhileOffline, allowTemporaryUse, actor, cancellationToken);
                break;
            case "preconfigure":
            case "save-assignment":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                await SaveDesiredRouteAsync(sourceId, portId, presetId, requestedMode, reserveWhileOffline, allowTemporaryUse, actor, cancellationToken);
                break;
            case "restore":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                if (DesiredRoutePolicy.HasSavedAssignment(route)) await ReconcileSavedRouteAsync(sourceId, cancellationToken);
                else if (route?.PortId is not null) await RestartReservedRouteAsync(route, cancellationToken, force: true);
                else await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "stop":
                await StopRouteAsync(sourceId, forceRelease: false, cancellationToken);
                break;
            case "remove-assignment":
                await RemoveDesiredRouteAsync(sourceId, actor, cancellationToken);
                break;
            case "restart":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                await StopRouteAsync(sourceId, forceRelease: true, cancellationToken);
                if (DesiredRoutePolicy.HasSavedAssignment(route)) await ReconcileSavedRouteAsync(sourceId, cancellationToken);
                else await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "reassign":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                ValidateRequestedPreset(presetId);
                await SaveDesiredRouteAsync(sourceId, portId, presetId, AssignmentMode.Manual,
                    reserveWhileOffline: true, allowTemporaryUse: false, actor, cancellationToken);
                break;
            case "reprobe":
                lock (_gate)
                {
                    if (_sources.TryGetValue(sourceId, out var source)) _sources[sourceId] = source with { State = SourceState.Probing, Media = null };
                    Interlocked.Exchange(ref _forceDiscoveryRequested, 1);
                }
                break;
            case "lock":
                if (route?.PortId is not null)
                {
                    var identity = SourceIdentityFromValue(route.SourceId);
                    _reservations.TryReserve(route.PortId, identity, true, DateTimeOffset.UtcNow, out _);
                    lock (_gate) _sourceMissingSince.Remove(route.SourceId);
                    await ReplaceRouteAsync(route with { Locked = true, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                }
                break;
            case "unlock":
                if (route?.PortId is not null)
                {
                    var identity = SourceIdentityFromValue(route.SourceId);
                    _reservations.Release(route.PortId, identity, force: true);
                    _reservations.TryReserve(route.PortId, identity, false, DateTimeOffset.UtcNow, out _);
                    lock (_gate)
                    {
                        if (!_sources.ContainsKey(route.SourceId)) _sourceMissingSince.TryAdd(route.SourceId, DateTimeOffset.UtcNow);
                    }
                    await ReplaceRouteAsync(route with { Locked = false, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                }
                break;
            case "standby":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                if (route is not null)
                {
                    if (_supervisor is not null) await _supervisor.StopAsync(SourceIdentityFromValue(sourceId), cancellationToken);
                    var fallback = route with { State = RouteState.Fallback, RetryAt = null, UpdatedAt = DateTimeOffset.UtcNow, FailureCategory = "OperatorStandby", FailureMessage = "Standby selected by operator." };
                    if (!_settings.SimulationMode) await StartFallbackAsync(fallback, cancellationToken);
                    await ReplaceRouteAsync(fallback, route.State, cancellationToken);
                }
                break;
            case "simulate-failure":
                _simulationFaults[sourceId] = "FFmpegCrash";
                break;
            case "simulate-stall":
                _simulationFaults[sourceId] = "VideoStall";
                break;
            case "clear-failure":
                _simulationFaults.TryRemove(sourceId, out _);
                if (route is not null) await ReplaceRouteAsync(route with { State = RouteState.Reconnecting, RetryAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action), $"Unknown route action '{action}'.");
        }
        await LogAsync("Information", "Operator", $"Action '{action}' requested.", sourceId, NewCorrelation(), cancellationToken);
        Publish($"{action} requested");
    }

    private void ValidateRequestedPreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return;
        lock (_gate) _ = OutputPresetSelection.Resolve(_settings.Presets, _settings.Presets.FirstOrDefault()?.Id ?? "", presetId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        _settings = await store.LoadSettingsAsync(stoppingToken);
        foreach (var source in await store.LoadSourcesAsync(stoppingToken))
            _sources[source.Identity.Value] = source with
            {
                State = source.State == SourceState.Disabled ? SourceState.Disabled : SourceState.PublisherDisconnected,
                LastObservedAt = source.LastObservedAt
            };
        foreach (var route in await store.LoadRoutesAsync(stoppingToken))
        {
            var migrated = DesiredRoutePolicy.MigrateLegacy(route);
            migrated = DesiredRoutePolicy.ResetTransientStateForStartup(migrated, DateTimeOffset.UtcNow);
            _routes[migrated.SourceId] = migrated;
            if (migrated != route) await store.SaveRouteAsync(migrated, route.State, stoppingToken);
            if (migrated.PortId is not null && migrated.State is not RouteState.Released and not RouteState.Disabled)
                _startupRecovery.Track(migrated.SourceId);
        }
        await LogAsync("Information", "Host", "BroadcastRouter server started; persisted routes will be reconciled.", cancellationToken: stoppingToken);
        await store.WriteConfigurationAuditAsync(new(0, DateTimeOffset.UtcNow, "ServiceRestart", "HOST", "", "",
            "Stopped", "Running", "system", "BroadcastRouter host startup", "Configuration loaded from SQLite"), stoppingToken);

        var fastInputSupervision = RunFastInputSupervisionAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    MarkCoordinatorProgress("Process supervision");
                    await MonitorProcessesAsync(stoppingToken);
                    MarkCoordinatorProgress("Source discovery and probing");
                    await DiscoverAndProbeAsync(stoppingToken);
                    MarkCoordinatorProgress("Media-tool and DeckLink validation");
                    await RefreshToolsAndPortsAsync(stoppingToken);
                    MarkCoordinatorProgress("Route reconciliation");
                    await ReconcileRoutesAsync(stoppingToken);
                    MarkCoordinatorProgress("Standby reconciliation");
                    await ReconcilePortStandbysAsync(stoppingToken);
                    _reconciliationFailureLogGate.Reset();
                    MarkCoordinatorProgress("Idle", completedCycle: true);
                    Publish("Running");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    var signature = $"{ex.GetType().FullName}:{ex.Message}";
                    var decision = _reconciliationFailureLogGate.Evaluate(signature, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
                    if (decision.ShouldLog)
                    {
                        var suppressed = decision.SuppressedCount > 0
                            ? $" {decision.SuppressedCount} identical failure(s) were suppressed during the previous minute."
                            : "";
                        logger.LogError(ex, "Router reconciliation cycle failed.{Suppressed}", suppressed);
                        await store.WriteLogAsync("Error", "Coordinator",
                            $"Reconciliation cycle failed: {ex.Message}.{suppressed}", cancellationToken: stoppingToken);
                    }
                    MarkCoordinatorProgress("Cycle fault handled", completedCycle: true);
                    Publish("Reconciliation degraded");
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        finally
        {
            try { await fastInputSupervision; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunFastInputSupervisionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var supervisor = _supervisor;
            if (supervisor is null || _settings.SimulationMode) continue;
            await SuperviseWowzaPublisherPresenceAsync(supervisor, cancellationToken);
            foreach (var observed in supervisor.Snapshot().Where(value =>
                         value.Purpose == RouteProcessPurpose.Live && value.Running))
            {
                var routeGate = _routeGates.GetOrAdd(observed.Source.Value, static _ => new SemaphoreSlim(1, 1));
                if (!await routeGate.WaitAsync(0, cancellationToken)) continue;
                try
                {
                    // Re-read both ownership and route state after acquiring the route gate.
                    // This prevents a stale snapshot from stopping a replacement process.
                    var current = supervisor.Snapshot().FirstOrDefault(value =>
                        value.Source == observed.Source && value.ProcessId == observed.ProcessId
                        && value.Purpose == RouteProcessPurpose.Live && value.Running);
                    if (current is null) continue;
                    RuntimeRoute? route;
                    lock (_gate) _routes.TryGetValue(observed.Source.Value, out route);
                    if (route is null || route.State is not (RouteState.Starting or RouteState.Running)) continue;

                    var failure = current.InputFailure;
                    var frozen = failure is null && IsInputFrozen(current, DateTimeOffset.UtcNow);
                    if (failure is null && !frozen) continue;

                    await supervisor.StopForOutputHandoffAsync(observed.Source, cancellationToken);
                    if (failure is not null)
                    {
                        await LogAsync("Warning", "InputLiveness",
                            $"FFmpeg process {observed.ProcessId} reported {failure.Category}; the exact owned session was reaped within the fast supervision path. {failure.Detail}",
                            route.SourceId, cancellationToken: cancellationToken);
                        await ScheduleRetryAsync(route, "InputSessionLost",
                            $"The live media session became unusable ({failure.Category}) and was recreated.", cancellationToken);
                    }
                    else
                    {
                        await LogAsync("Warning", "InputLiveness",
                            $"FFmpeg process {observed.ProcessId} produced a duplicate-dominated burst; the stale decoder was reaped within the fast supervision path.",
                            route.SourceId, cancellationToken: cancellationToken);
                        await ScheduleRetryAsync(route, "InputFrozen",
                            "A rapid duplicate-frame burst indicated a short RTSP interruption; the decoder session was recreated.", cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await LogAsync("Error", "FastInputSupervision",
                        $"Rapid recovery for owned FFmpeg process {observed.ProcessId} failed; the normal reconciler will retry. {LogRedactor.Redact(ex.Message)}",
                        observed.Source.Value, cancellationToken: cancellationToken);
                }
                finally { routeGate.Release(); }
            }

            foreach (var route in RoutesCopy().Where(value =>
                         value.State is RouteState.Reconnecting or RouteState.Fallback
                         && value.RetryAt is not null && value.RetryAt <= DateTimeOffset.UtcNow))
            {
                DiscoveredSource? source;
                lock (_gate) _sources.TryGetValue(route.SourceId, out source);
                if (!RapidStreamRecoveryPolicy.CanAttemptReservedRecovery(route, source)) continue;
                await RestartReservedRouteAsync(route, cancellationToken);
            }
        }
    }

    private async Task SuperviseWowzaPublisherPresenceAsync(
        FfmpegProcessSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var supervisedRoutes = RoutesCopy().Where(route =>
            route.State is RouteState.Starting or RouteState.Running
            || (DesiredRoutePolicy.HasSavedAssignment(route)
                && route.State is RouteState.Reconnecting or RouteState.Fallback)).ToArray();
        if (supervisedRoutes.Length == 0) return;

        var sources = SourcesCopy().ToDictionary(source => source.Identity.Value, StringComparer.Ordinal);
        foreach (var profile in settings.WowzaServers.Where(profile => profile.Enabled))
        {
            var candidates = supervisedRoutes.Where(route =>
                    sources.TryGetValue(route.SourceId, out var source)
                    && source.Identity.ServerId.Equals(profile.ServerId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length == 0) continue;

            var poll = await PollWowzaServerAsync(profile, cancellationToken);
            if (poll.Error is not null) continue;
            var connected = poll.Discovered.Where(source => source.State == SourceState.PublisherActive)
                .Select(source => source.Identity.Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                var publisherConnected = connected.Contains(candidate.SourceId);
                if (publisherConnected)
                {
                    if (!_publisherDisconnectDetector.ObserveConnected(candidate.SourceId)) continue;
                    var recoveryGate = _routeGates.GetOrAdd(candidate.SourceId, static _ => new SemaphoreSlim(1, 1));
                    if (!await recoveryGate.WaitAsync(0, cancellationToken)) continue;
                    try
                    {
                        RuntimeRoute? recoveryRoute;
                        DiscoveredSource? recoverySource;
                        lock (_gate)
                        {
                            _routes.TryGetValue(candidate.SourceId, out recoveryRoute);
                            _sources.TryGetValue(candidate.SourceId, out recoverySource);
                        }
                        if (recoveryRoute is null || recoverySource is null
                            || !DesiredRoutePolicy.HasSavedAssignment(recoveryRoute)
                            || recoveryRoute.State is not (RouteState.Reconnecting or RouteState.Fallback)) continue;

                        var now = DateTimeOffset.UtcNow;
                        var publisherRestored = recoverySource with
                        {
                            State = SourceState.PublisherActive,
                            LastObservedAt = now
                        };
                        lock (_gate)
                        {
                            _sources[candidate.SourceId] = publisherRestored;
                            _sourceMissingSince.Remove(candidate.SourceId);
                        }
                        await store.UpsertSourceAsync(publisherRestored, cancellationToken);
                        await ReplaceRouteAsync(recoveryRoute with
                        {
                            RetryAt = now,
                            FailureCategory = "PublisherRestored",
                            FailureMessage = "Wowza confirmed that the publisher returned; reserved-route recovery was accelerated.",
                            UpdatedAt = now
                        }, recoveryRoute.State, cancellationToken);
                        await LogAsync("Information", "PublisherPresence",
                            "Wowza confirmed that the publisher returned; the reserved route was made immediately eligible for live recovery.",
                            candidate.SourceId, cancellationToken: cancellationToken);
                    }
                    finally { recoveryGate.Release(); }
                    continue;
                }

                if (!_publisherDisconnectDetector.Observe(candidate.SourceId, publisherConnected: false)) continue;

                var routeGate = _routeGates.GetOrAdd(candidate.SourceId, static _ => new SemaphoreSlim(1, 1));
                if (!await routeGate.WaitAsync(0, cancellationToken)) continue;
                try
                {
                    RuntimeRoute? route;
                    DiscoveredSource? source;
                    lock (_gate)
                    {
                        _routes.TryGetValue(candidate.SourceId, out route);
                        _sources.TryGetValue(candidate.SourceId, out source);
                    }
                    if (route is null || source is null
                        || route.State is not (RouteState.Starting or RouteState.Running)) continue;

                    var current = supervisor.Snapshot().FirstOrDefault(value =>
                        value.Source == source.Identity && value.Purpose == RouteProcessPurpose.Live && value.Running);
                    if (current is null) continue;

                    await supervisor.StopForOutputHandoffAsync(source.Identity, cancellationToken);
                    lock (_gate)
                    {
                        _sources[candidate.SourceId] = source with
                        {
                            State = SourceState.PublisherDisconnected,
                            LastObservedAt = DateTimeOffset.UtcNow
                        };
                        _sourceReadySince.Remove(candidate.SourceId);
                        _sourceMissingSince[candidate.SourceId] = DateTimeOffset.UtcNow;
                    }
                    await LogAsync("Warning", "PublisherPresence",
                        $"Wowza reported the publisher missing twice within the 100 ms supervision loop; FFmpeg process {current.ProcessId} was reaped before stale SDI output could persist.",
                        candidate.SourceId, cancellationToken: cancellationToken);
                    await ScheduleRetryAsync(route, "PublisherDisconnected",
                        "Wowza reported a rapid publisher interruption; silent standby is active while the saved route retries.",
                        cancellationToken);
                }
                finally { routeGate.Release(); }
            }
        }
    }

    private void MarkCoordinatorProgress(string stage, bool completedCycle = false)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_livenessGate)
        {
            _coordinatorStage = stage;
            _coordinatorProgressAt = now;
            if (!completedCycle) return;
            _lastCompletedCycleAt = now;
            _completedCycles++;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisor is not null) await _supervisor.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task RefreshToolsAndPortsAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        DeckLinkPort[] previousPorts;
        lock (_gate) previousPorts = _ports.Values.ToArray();
        if (settings.SimulationMode)
        {
            _validation = new(ToolValidationState.Valid, "simulation-ffmpeg 1.0", "simulation-ffprobe 1.0", true, true, 4,
                ["Simulation mode: no real FFmpeg or DeckLink device is opened."], DateTimeOffset.UtcNow,
                WindowsDeckLinkSafeTerminateSupported: true, PortStandbySupported: true);
            var simulatedPorts = ApplyOverrides(BuildSimulationPorts(), settings.DeckLinkCardOverrides, settings.DeckLinkPortOverrides);
            lock (_gate)
            {
                _ports.Clear();
                foreach (var port in simulatedPorts) _ports[port.StableId] = port;
            }
            foreach (var port in simulatedPorts) await store.UpsertPortAsync(port, cancellationToken);
            _forceToolValidation = false;
            return;
        }

        if (!_forceToolValidation && DateTimeOffset.UtcNow - _lastToolValidation < TimeSpan.FromMinutes(5)) return;
        _validation = new(ToolValidationState.Validating, null, null, false, false, 0, ["Validation in progress."], DateTimeOffset.UtcNow);
        Publish("Validating media tools");
        _validation = await MediaToolValidator.ValidateAsync(settings.MediaTools, cancellationToken);
        _lastToolValidation = DateTimeOffset.UtcNow;
        _forceToolValidation = false;
        IReadOnlyList<DeckLinkPort> rawPorts = [];
        if (_validation.DeckLinkCompiled && File.Exists(settings.MediaTools.FfmpegPath))
            rawPorts = await new FfmpegDeckLinkEnumerator(settings.MediaTools.FfmpegPath).EnumerateAsync(cancellationToken);
        var aliases = DeckLinkIdentityMigration.BuildAliasMap(rawPorts);
        var liveOwnershipExists = _reservations.Snapshot().Count > 0
            || (_supervisor?.Snapshot().Any(process => process.Running) ?? false);
        var legacyReferencesExist = DeckLinkIdentityMigration.HasLegacyReferences(settings, RoutesCopy(), aliases);
        if (aliases.Count > 0 && liveOwnershipExists && legacyReferencesExist)
        {
            rawPorts = DeckLinkIdentityMigration.DeferUntilRestart(rawPorts);
            aliases = DeckLinkIdentityMigration.BuildAliasMap(rawPorts);
            await LogAsync("Warning", "DeckLinkIdentity",
                "Persistent DeckLink IDs were detected while output ownership was active; migration is deferred until the next controlled host restart.",
                cancellationToken: cancellationToken);
        }
        string[] standbyOwnedPortIds;
        lock (_gate) standbyOwnedPortIds = _standbys
            .Where(value => value.Value.State is PortStandbyState.Starting or PortStandbyState.Running or PortStandbyState.Live)
            .Select(value => value.Key).ToArray();
        var ownedPortIds = _reservations.Snapshot().Select(value => value.PortId)
            .Concat(standbyOwnedPortIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveredIds = rawPorts.Select(port => port.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedOwnedPorts = previousPorts.Where(port => ownedPortIds.Contains(port.StableId) && !discoveredIds.Contains(port.StableId)).ToArray();
        if (retainedOwnedPorts.Length > 0)
        {
            rawPorts = rawPorts.Concat(retainedOwnedPorts).ToArray();
            await LogAsync("Warning", "DeckLinkDiscovery",
                $"A transient rescan omitted {retainedOwnedPorts.Length} connector(s) with active ownership; their prior identities were retained and no route or standby was released.",
                cancellationToken: cancellationToken);
        }
        if (aliases.Count > 0)
            settings = await MigrateDeckLinkIdentityReferencesAsync(settings, rawPorts, aliases, cancellationToken);
        var ports = ApplyOverrides(rawPorts, settings.DeckLinkCardOverrides, settings.DeckLinkPortOverrides);
        if (aliases.Count > 0)
            await MigrateRoutePortIdsAsync(ports, aliases, cancellationToken);
        lock (_gate)
        {
            _ports.Clear();
            foreach (var port in ports) _ports[port.StableId] = port;
        }
        foreach (var port in ports) await store.UpsertPortAsync(port, cancellationToken);
        await AuditDeviceRediscoveryAsync(previousPorts, ports, cancellationToken);
        await LogAsync(_validation.CanStartHardwareRoutes ? "Information" : "Error", "MediaTools",
            _validation.CanStartHardwareRoutes ? $"Validation passed; {ports.Count} DeckLink output(s) available." : "Validation failed; hardware routes are blocked.", cancellationToken: cancellationToken);
    }

    private async Task<OperatorSettings> MigrateDeckLinkIdentityReferencesAsync(
        OperatorSettings settings,
        IReadOnlyList<DeckLinkPort> ports,
        IReadOnlyDictionary<string, string> aliases,
        CancellationToken cancellationToken)
    {
        await _settingsMutationGate.WaitAsync(cancellationToken);
        try
        {
            var current = GetSettings();
            var previous = Clone(current);
            if (!DeckLinkIdentityMigration.MigrateSettings(current, aliases, ports)) return current;
            var appliedAt = DateTimeOffset.UtcNow;
            SettingsConcurrencyPolicy.MarkApplied(current, previous.ConfigurationRevision, appliedAt, "system:DeckLinkIdentity");
            await store.SaveSettingsAsync(current, cancellationToken);
            lock (_gate) _settings = Clone(current);
            foreach (var alias in aliases.Where(alias => previous.DeckLinkPortOverrides.Any(value =>
                         value.StableId.Equals(alias.Key, StringComparison.OrdinalIgnoreCase))))
            {
                var migratedPort = ports.FirstOrDefault(port => port.StableId.Equals(alias.Value, StringComparison.OrdinalIgnoreCase));
                await store.WriteConfigurationAuditAsync(new(0, appliedAt, "IdentityMigration", alias.Value,
                    migratedPort is null ? "" : DeckLinkDisplayName.Card(migratedPort),
                    migratedPort is null ? "" : DeckLinkDisplayName.Connector(migratedPort),
                    alias.Key, alias.Value, "system", "Persistent DeckLink identity migration",
                    $"Persisted in configuration revision {current.ConfigurationRevision}"), cancellationToken);
            }
            await LogAsync("Information", "DeckLinkIdentity",
                $"Migrated saved DeckLink references to {ports.Count(port => port.PersistentId is not null)} persistent hardware ID(s) in configuration revision {current.ConfigurationRevision}.",
                cancellationToken: cancellationToken);
            return current;
        }
        finally { _settingsMutationGate.Release(); }
    }

    private async Task MigrateRoutePortIdsAsync(
        IReadOnlyList<DeckLinkPort> ports,
        IReadOnlyDictionary<string, string> aliases,
        CancellationToken cancellationToken)
    {
        var byId = ports.ToDictionary(port => port.StableId, StringComparer.OrdinalIgnoreCase);
        RuntimeRoute[] routes;
        lock (_gate) routes = _routes.Values.ToArray();
        var migrated = routes
            .Select(route => DeckLinkIdentityMigration.MigrateRoute(route, aliases, byId))
            .Where(route => routes.Any(original => original.SourceId == route.SourceId
                && (!string.Equals(original.PortId, route.PortId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(original.DesiredPortId, route.DesiredPortId, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        foreach (var route in migrated)
        {
            lock (_gate) _routes[route.SourceId] = route;
            await store.SaveRouteAsync(route, route.State, cancellationToken);
        }
        if (migrated.Length > 0)
            await LogAsync("Information", "DeckLinkIdentity",
                $"Migrated {migrated.Length} persisted route assignment(s) to persistent DeckLink hardware IDs.",
                cancellationToken: cancellationToken);
    }

    private async Task<DiscoveryCycleResult?> DiscoverAndProbeAsync(CancellationToken cancellationToken, bool force = false)
    {
        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
        var settings = GetSettings();
        var minimumPoll = settings.WowzaServers.Where(x => x.Enabled).Select(x => Math.Clamp(x.PollingIntervalSeconds, 1, 300)).DefaultIfEmpty(2).Min();
        var explicitlyRequested = Interlocked.Exchange(ref _forceDiscoveryRequested, 0) != 0;
        if (!force && !explicitlyRequested && DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromSeconds(minimumPoll)) return null;
        _lastDiscovery = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();

        var observations = new List<DiscoveredSource>();
        var successfullyPolledServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.SimulationMode) observations.AddRange(BuildSimulationSources());
        else
        {
            var enabledProfiles = settings.WowzaServers.Where(x => x.Enabled).ToArray();
            var polls = await Task.WhenAll(enabledProfiles.Select(profile => PollWowzaServerAsync(profile, cancellationToken)));
            foreach (var poll in polls)
            {
                lock (_gate) _servers[poll.Profile.ServerId] = poll.Health;
                if (poll.Error is null)
                {
                    observations.AddRange(poll.Discovered);
                    successfullyPolledServerIds.Add(poll.Profile.ServerId);
                    if (poll.Recovered)
                        await LogAsync("Information", "WowzaDiscovery",
                            $"{poll.Profile.ServerId} discovery recovered; {poll.Discovered.Count} incoming stream record(s) were received.",
                            cancellationToken: cancellationToken);
                }
                else
                {
                    await LogAsync("Warning", "WowzaDiscovery",
                        $"{poll.Profile.ServerId} discovery failed: {poll.Error}. Healthy routes were retained.",
                        cancellationToken: cancellationToken);
                }
            }
        }

        foreach (var manual in settings.ManualSources)
        {
            if (!Uri.TryCreate(manual.RtspUrl, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp") continue;
            var identity = new SourceIdentity("MANUAL", "manual", "_definst_", manual.StableId);
            observations.Add(new(identity, manual.FriendlyName, uri, manual.Enabled ? SourceState.PublisherActive : SourceState.Disabled, manual.Priority, Tags: new HashSet<string> { "manual" },
                FixedPortId: EmptyToNull(manual.FixedPortId), AssignmentLocked: manual.Locked, LastObservedAt: DateTimeOffset.UtcNow));
        }

        var enabledServerIds = settings.WowzaServers.Where(profile => profile.Enabled).Select(profile => profile.ServerId).ToArray();
        var staleSources = SourceObservationReconciler.FindStaleSources(
            SourcesCopy(), observations, enabledServerIds, successfullyPolledServerIds, settings.SimulationMode);
        if (staleSources.Count > 0)
        {
            var routeStates = RoutesCopy().ToDictionary(route => route.SourceId, StringComparer.Ordinal);
            var renamedSources = staleSources.Where(stale => !enabledServerIds.Contains(stale.Identity.ServerId, StringComparer.OrdinalIgnoreCase)
                && (!routeStates.TryGetValue(stale.Identity.Value, out var staleRoute)
                    || staleRoute.State is RouteState.Released or RouteState.Disabled)
                && observations.Any(observation =>
                    observation.Identity.ServerId != stale.Identity.ServerId
                    && observation.Identity.Application == stale.Identity.Application
                    && observation.Identity.ApplicationInstance == stale.Identity.ApplicationInstance
                    && observation.Identity.StreamName == stale.Identity.StreamName))
                .ToArray();
            var renamedIds = renamedSources.Select(source => source.Identity.Value).ToHashSet(StringComparer.Ordinal);
            var retainedOffline = staleSources.Where(stale => !renamedIds.Contains(stale.Identity.Value)).ToArray();
            var transitionedOffline = retainedOffline.Where(stale => stale.State != SourceState.PublisherDisconnected).ToArray();
            lock (_gate)
            {
                foreach (var renamed in renamedSources)
                {
                    _sources.Remove(renamed.Identity.Value);
                    _waiting.Remove(renamed.Identity);
                    _sourceReadySince.Remove(renamed.Identity.Value);
                    _sourceMissingSince.Remove(renamed.Identity.Value);
                }
                foreach (var stale in retainedOffline)
                {
                    _sources[stale.Identity.Value] = stale with
                    {
                        State = SourceState.PublisherDisconnected,
                        Media = stale.Media,
                        LastObservedAt = stale.LastObservedAt
                    };
                    _waiting.Remove(stale.Identity);
                    _sourceReadySince.Remove(stale.Identity.Value);
                    _sourceMissingSince.TryAdd(stale.Identity.Value, DateTimeOffset.UtcNow);
                }
            }
            foreach (var renamed in renamedSources)
                await store.DeleteSourceAsync(renamed.Identity.Value, cancellationToken);
            foreach (var stale in transitionedOffline)
                await store.UpsertSourceAsync(stale with { State = SourceState.PublisherDisconnected }, cancellationToken);
            if (renamedSources.Length > 0)
                await LogAsync("Information", "Discovery",
                    $"Removed {renamedSources.Length} obsolete source identity record(s) after a server identity change.",
                    cancellationToken: cancellationToken);
            if (transitionedOffline.Length > 0)
                await LogAsync("Information", "Discovery",
                    $"Retained {transitionedOffline.Length} inactive incoming stream(s) in the routing inventory.",
                    cancellationToken: cancellationToken);
        }

        lock (_gate)
        {
            var activeHealthIds = enabledServerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (settings.SimulationMode) activeHealthIds.Add("SIM-WOWZA");
            foreach (var staleServerId in _servers.Keys.Where(id => !activeHealthIds.Contains(id)).ToArray())
                _servers.Remove(staleServerId);
        }

        using var probeConcurrency = new SemaphoreSlim(8, 8);
        var probeTasks = observations.Select(async source =>
        {
            await probeConcurrency.WaitAsync(cancellationToken);
            try { return await ProbeObservationAsync(source, settings, cancellationToken); }
            finally { probeConcurrency.Release(); }
        }).ToArray();
        var probeOutcomes = await Task.WhenAll(probeTasks);
        foreach (var outcome in probeOutcomes)
        {
            var probed = outcome.Source;
            if (probed.State is SourceState.PublisherDisconnected or SourceState.RtspUnavailable or SourceState.Disabled)
                lock (_gate) _sourceMissingSince.TryAdd(probed.Identity.Value, DateTimeOffset.UtcNow);
            else
                lock (_gate) _sourceMissingSince.Remove(probed.Identity.Value);
            if (outcome.Probe is { } probe)
            {
                if (probed.State != SourceState.Ready)
                    await LogAsync("Warning", "Probe", $"Probe failed: {probe.FailureCategory}: {probe.Detail}", probed.Identity.Value, cancellationToken: cancellationToken);
                else if (outcome.ExtendedVideoConfirmed)
                    await LogAsync("Information", "Probe",
                        "Extended keyframe acquisition confirmed sustained decoded video after the quick probe saw only sparse frames.",
                        probed.Identity.Value, cancellationToken: cancellationToken);
                else if (outcome.CurrentMode == SourceMediaMode.AudioLed
                         && (outcome.PreviousMode == SourceMediaMode.Unknown || outcome.ModeChanged))
                    await LogAsync("Information", "Probe", $"Audio-led source accepted: {probe.Detail}", probed.Identity.Value, cancellationToken: cancellationToken);
                else if (outcome.ModeChanged && outcome.CurrentMode == SourceMediaMode.Video)
                    await LogAsync("Information", "Probe", "Sustained decoded video was confirmed twice; live video playout is restored from audio-led black mode.",
                        probed.Identity.Value, cancellationToken: cancellationToken);
            }
            lock (_gate)
            {
                _sources[probed.Identity.Value] = probed;
                if (probed.State == SourceState.Ready)
                {
                    _sourceReadySince.TryAdd(probed.Identity.Value, DateTimeOffset.UtcNow);
                    _sourceMissingSince.Remove(probed.Identity.Value);
                }
                else
                {
                    _sourceReadySince.Remove(probed.Identity.Value);
                    _sourceMissingSince.TryAdd(probed.Identity.Value, DateTimeOffset.UtcNow);
                }
            }
            await store.UpsertSourceAsync(probed, cancellationToken);
        }

        await ReleaseExpiredMissingRoutesAsync(settings, cancellationToken);

        if (settings.SimulationMode)
            lock (_gate) _servers["SIM-WOWZA"] = new("SIM-WOWZA", "Simulated Wowza", true, true, observations.Count, "Simulation API healthy.", DateTimeOffset.UtcNow);
        elapsed.Stop();
        return new(observations.Count, settings.WowzaServers.Count(profile => profile.Enabled),
            successfullyPolledServerIds.Count, elapsed.Elapsed);
        }
        finally { _discoveryGate.Release(); }
    }

    private async Task<ProbeOutcome> ProbeObservationAsync(DiscoveredSource source, OperatorSettings settings,
        CancellationToken cancellationToken)
    {
        if (source.State is SourceState.PublisherDisconnected or SourceState.Disabled)
        {
            lock (_gate)
            {
                _sourceMediaModes.Remove(source.Identity.Value);
                _lastExtendedVideoProbeAt.Remove(source.Identity.Value);
                _pendingMediaModeRestarts.Remove(source.Identity.Value);
            }
            return new(source, null, SourceMediaMode.Unknown, SourceMediaMode.Unknown, false, false);
        }
        if (source.Media is not null)
            return new(source, null, SourceMediaMode.Unknown, SourceMediaMode.Unknown, false, false);

        SourceMediaModeState previousMode;
        lock (_gate)
        {
            if (!_sourceMediaModes.TryGetValue(source.Identity.Value, out previousMode!))
            {
                previousMode = _sources.TryGetValue(source.Identity.Value, out var previous)
                    && previous.State == SourceState.Ready
                    ? previous.Media switch
                    {
                        { HasUsableVideo: true } media => new(SourceMediaMode.Video, 0, 0, media),
                        { HasUsableVideo: false, AudioCodec: not null } => new(SourceMediaMode.AudioLed, 0, 0, null),
                        _ => SourceMediaModeState.Unknown
                    }
                    : SourceMediaModeState.Unknown;
            }
        }
        var quickProbe = settings.SimulationMode
            ? await new SimulationStreamProbe().ProbeAsync(source.RtspUri, cancellationToken)
            : await new FfprobeStreamProbe(settings.MediaTools.FfprobePath, TimeSpan.FromSeconds(8)).ProbeAsync(source.RtspUri, cancellationToken);
        var rawProbe = quickProbe;
        var extendedVideoConfirmed = false;
        if (!settings.SimulationMode
            && SourceProbeReadinessPolicy.NeedsExtendedVideoConfirmation(quickProbe)
            && ShouldRunExtendedVideoProbe(source.Identity.Value))
        {
            var extended = await new FfprobeStreamProbe(
                    settings.MediaTools.FfprobePath,
                    ExtendedVideoProbeTimeout,
                    ExtendedVideoProbeDuration)
                .ProbeAsync(source.RtspUri, cancellationToken);
            rawProbe = SourceProbeReadinessPolicy.PreferExtendedVideoEvidence(quickProbe, extended);
            extendedVideoConfirmed = !ReferenceEquals(rawProbe, quickProbe)
                && rawProbe.FramesReceived
                && rawProbe.Media is { HasUsableVideo: true };
        }
        var decision = SourceProbeReadinessPolicy.ObserveMediaMode(previousMode, rawProbe,
            videoConfirmationProbes: extendedVideoConfirmed ? 1 : 2);
        lock (_gate)
        {
            _sourceMediaModes[source.Identity.Value] = decision.State;
            if (decision.ModeChanged) _pendingMediaModeRestarts.Add(source.Identity.Value);
        }
        var probe = decision.EffectiveProbe;
        return new(source with { State = SourceProbeReadinessPolicy.Resolve(probe), Media = probe.Media }, probe,
            previousMode.Mode, decision.State.Mode, decision.ModeChanged, extendedVideoConfirmed);
    }

    private bool ShouldRunExtendedVideoProbe(string sourceId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_lastExtendedVideoProbeAt.TryGetValue(sourceId, out var last)
                && now - last < ExtendedVideoProbeInterval)
                return false;
            _lastExtendedVideoProbeAt[sourceId] = now;
            return true;
        }
    }

    private async Task<WowzaPollResult> PollWowzaServerAsync(WowzaServerProfile profile,
        CancellationToken cancellationToken)
    {
        ServerHealth? previousHealth;
        lock (_gate) _servers.TryGetValue(profile.ServerId, out previousHealth);
        try
        {
            var password = string.IsNullOrWhiteSpace(profile.ProtectedPassword)
                ? ""
                : WindowsDpapi.Unprotect(profile.ProtectedPassword);
            var server = ToConfiguration(profile);
            using var client = httpClientFactory.CreateClient(profile.ValidateTlsCertificate
                ? "WowzaValidated"
                : "WowzaInsecure");
            var provider = new WowzaDiscoveryProvider(client, server,
                new StaticCredentialResolver(new CredentialValue(profile.Username, password)));
            var discovered = await provider.DiscoverAsync(cancellationToken);
            var health = new ServerHealth(profile.ServerId, profile.FriendlyName, true, true,
                discovered.Count, "Discovery succeeded.", DateTimeOffset.UtcNow);
            return new(profile, discovered, health, previousHealth is { Reachable: false }, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = LogRedactor.Redact(ex.Message);
            var health = new ServerHealth(profile.ServerId, profile.FriendlyName, false, false, 0,
                error, DateTimeOffset.UtcNow);
            return new(profile, [], health, false, error);
        }
    }

    private async Task ReconcileRoutesAsync(CancellationToken cancellationToken)
    {
        if (_emergencyStopped) return;
        var settings = GetSettings();
        await RestoreReservationsAndProcessesAsync(settings, cancellationToken);
        await RestartRoutesForMediaModeChangesAsync(cancellationToken);

        var savedRoutes = RoutesCopy().Where(DesiredRoutePolicy.HasSavedAssignment)
            .OrderByDescending(route => DesiredRoutePolicy.PriorityRank(route.AssignmentMode))
            .ThenByDescending(route => route.Priority)
            .ToArray();
        foreach (var saved in savedRoutes)
            await ReconcileSavedRouteAsync(saved.SourceId, cancellationToken);

        foreach (var source in SourcesCopy().Where(source => source.State != SourceState.Disabled))
        {
            RuntimeRoute? current;
            lock (_gate) _routes.TryGetValue(source.Identity.Value, out current);
            if (DesiredRoutePolicy.HasSavedAssignment(current)) continue;
            var defaultPreset = settings.Presets.FirstOrDefault()?.Id ?? "";
            var decision = RoutingRuleEvaluator.Evaluate(source, settings.Rules, defaultPreset);
            if (!string.IsNullOrWhiteSpace(decision.FixedPortId))
                await SaveDesiredRouteAsync(source.Identity.Value, decision.FixedPortId, decision.PresetId,
                    AssignmentMode.Preconfigured, reserveWhileOffline: true, allowTemporaryUse: false,
                    "system:routing-rule", cancellationToken);
        }

        if (!settings.Routing.AutomaticRoutingEnabled) return;
        var readySources = SourcesCopy().Where(x => x.State == SourceState.Ready && x.AutomaticRoutingEnabled).ToArray();
        var readyById = readySources.ToDictionary(x => x.Identity.Value, StringComparer.Ordinal);
        var queued = _waiting.Snapshot();
        var queuedIds = queued.Select(x => x.Source.Value).ToHashSet(StringComparer.Ordinal);
        var sources = queued.Where(item => readyById.ContainsKey(item.Source.Value)).Select(item => readyById[item.Source.Value])
            .Concat(readySources.Where(source => !queuedIds.Contains(source.Identity.Value)).OrderByDescending(source => source.Priority))
            .ToArray();
        foreach (var source in sources)
        {
            RuntimeRoute? route;
            lock (_gate) _routes.TryGetValue(source.Identity.Value, out route);
            if (DesiredRoutePolicy.HasSavedAssignment(route)) continue;
            if (route is null || route.State is RouteState.Released or RouteState.Known or RouteState.Ready or RouteState.WaitingForPort)
                await EnsureRouteAsync(source.Identity.Value, null, null, manual: false, cancellationToken);
            else if (route.State is RouteState.Reconnecting or RouteState.Fallback
                     && route.RetryAt <= DateTimeOffset.UtcNow
                     && SourceHasBeenReadyLongEnough(route.SourceId, settings.Routing.StableRestoreSeconds))
                await RestartReservedRouteAsync(route, cancellationToken);
        }
    }

    private async Task SaveDesiredRouteAsync(string sourceId, string? portId, string? presetId, AssignmentMode mode,
        bool reserveWhileOffline, bool allowTemporaryUse, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(portId))
            throw new InvalidOperationException("Select a port marked as an output port before saving the routing entry.");
        if (mode is not AssignmentMode.Preconfigured and not AssignmentMode.Manual)
            mode = AssignmentMode.Manual;
        // Saved assignments are fail-closed: the desired port remains protected unless
        // the operator explicitly opts into temporary automatic use.
        reserveWhileOffline = !allowTemporaryUse || reserveWhileOffline;

        DiscoveredSource source;
        DeckLinkPort port;
        OutputPresetProfile preset;
        RuntimeRoute? previous;
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out source!))
                throw new InvalidOperationException("The incoming stream is not present in the configured/discovered stream list.");
            if (!_ports.TryGetValue(portId, out port!)) throw new InvalidOperationException("The selected DeckLink port is unavailable.");
            if (!port.IsOutputPort) throw new InvalidOperationException("The selected DeckLink port is not marked as an output port.");
            preset = OutputPresetSelection.Resolve(_settings.Presets, _settings.Presets.FirstOrDefault()?.Id ?? "", presetId);
            _routes.TryGetValue(sourceId, out previous);
        }

        if (previous is not null && (previous.PortId is not null
            || previous.State is RouteState.Starting or RouteState.Running or RouteState.Reconnecting or RouteState.Fallback))
        {
            await StopRouteAsync(sourceId, forceRelease: true, cancellationToken, preserveDesiredAssignment: false);
            lock (_gate) _routes.TryGetValue(sourceId, out previous);
        }

        var now = DateTimeOffset.UtcNow;
        var route = new RuntimeRoute(sourceId, source.FriendlyName, null, null, preset.Id,
            RouteState.WaitingForStream, mode, reserveWhileOffline && !allowTemporaryUse,
            source.Priority, previous?.RestartCount ?? 0, null, null, null, 0, 0, null, now, null, null,
            DesiredPortId: port.StableId, DesiredPortName: DeckLinkDisplayName.Full(port),
            ReserveWhileOffline: reserveWhileOffline, AllowTemporaryUse: allowTemporaryUse);
        await ReplaceRouteAsync(route, previous?.State, cancellationToken);
        await ReconcileSavedRouteAsync(sourceId, cancellationToken);
        await store.WriteConfigurationAuditAsync(new(0, DateTimeOffset.UtcNow, "RoutingAssignment", port.StableId,
            DeckLinkDisplayName.Card(port), DeckLinkDisplayName.Connector(port), DescribeRouteAssignment(previous),
            DescribeRouteAssignment(route), actor, $"{mode} routing assignment saved", "Persisted and applied", sourceId), cancellationToken);
    }

    private async Task ReconcileSavedRouteAsync(string sourceId, CancellationToken cancellationToken)
    {
        var routeGate = _routeGates.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1));
        await routeGate.WaitAsync(cancellationToken);
        try
        {
            RuntimeRoute? route;
            DiscoveredSource? source;
            DeckLinkPort? port;
            OutputPresetProfile? preset;
            lock (_gate)
            {
                _routes.TryGetValue(sourceId, out route);
                _sources.TryGetValue(sourceId, out source);
                port = route?.DesiredPortId is null ? null : _ports.GetValueOrDefault(route.DesiredPortId);
                preset = route is null ? null : _settings.Presets.FirstOrDefault(value => value.Id == route.PresetId);
            }
            if (route is null || !DesiredRoutePolicy.HasSavedAssignment(route)) return;
            if (source is null || port is null || preset is null || !port.IsOutputPort)
            {
                if (route.PortId is not null)
                {
                    var routeIdentity = SourceIdentityFromValue(route.SourceId);
                    if (_supervisor is not null) await _supervisor.StopAsync(routeIdentity, cancellationToken);
                    _reservations.Release(route.PortId, routeIdentity, force: true);
                }
                await ReplaceRouteAsync(route with
                {
                    State = RouteState.WaitingForPort,
                    PortId = null,
                    PortName = null,
                    FailureCategory = "RoutingConflict",
                    FailureMessage = port is not null && !port.IsOutputPort
                        ? "The saved port is no longer marked as an output port."
                        : "The saved source, port, or output preset is unavailable.",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken);
                return;
            }
            if (!Compatible(port, preset, requiredGroup: null))
            {
                await ReplaceRouteAsync(route with
                {
                    State = RouteState.WaitingForPort,
                    PortId = null,
                    PortName = null,
                    FailureCategory = "RoutingConflict",
                    FailureMessage = "The saved output port does not support the selected output preset.",
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken);
                return;
            }

            var ownsLease = _reservations.Snapshot().Any(value =>
                value.PortId.Equals(port.StableId, StringComparison.OrdinalIgnoreCase)
                && value.Source.Value == route.SourceId);
            var ownedProcess = _settings.SimulationMode
                ? null
                : _supervisor?.Snapshot().FirstOrDefault(value => value.Source.Value == route.SourceId && value.Running);
            // A healthy owned decoder is stronger evidence than an overlapping FFprobe timeout.
            // This prevents a slow discovery sample from tearing down a route that has already
            // reconnected and is producing actual source video.
            var ownedLiveVideoIsAdvancing = ownedProcess is
                {
                    Purpose: RouteProcessPurpose.Live,
                    Running: true,
                    Progress.Frame: > 0
                }
                && source.Media?.HasUsableVideo == true;
            var active = RapidStreamRecoveryPolicy.IsEffectivelyActive(source, ownedLiveVideoIsAdvancing);
            var ownsLiveProcess = _settings.SimulationMode || ownedProcess?.Purpose == RouteProcessPurpose.Live;
            if (active && (route.State is RouteState.Starting or RouteState.Running) && ownsLease && ownsLiveProcess)
                return;
            if (MissingRouteProcessRecoveryPolicy.RequiresRetry(active, route.State, ownsLiveProcess))
            {
                await ScheduleRetryAsync(route, "ProcessExited",
                    "The live FFmpeg owner exited before route reconciliation; fallback and a fresh live session will be started.",
                    cancellationToken);
                return;
            }
            if (active && (route.State is RouteState.Reconnecting or RouteState.Fallback)
                && route.RetryAt is not null && route.RetryAt > DateTimeOffset.UtcNow)
                return;
            if (!active && DesiredRoutePolicy.HasSavedAssignment(route)
                && route.State is RouteState.Reconnecting or RouteState.Fallback
                && route.RetryAt is not null)
                return;
            var recoveryStartupGrace = TimeSpan.FromMilliseconds(
                Math.Clamp(_settings.Routing.InputReadTimeoutMilliseconds, 500, 30000) + 2000);
            if (ownedProcess is not null && RapidStreamRecoveryPolicy.ShouldKeepStartingAttempt(
                    route, active, ownsLiveProcess, ownedProcess.StartedAt,
                    DateTimeOffset.UtcNow, recoveryStartupGrace))
                return;
            if (RapidStreamRecoveryPolicy.ShouldEnterSavedRetry(route, active))
            {
                if (_supervisor is not null)
                    await _supervisor.StopForOutputHandoffAsync(source.Identity, cancellationToken);
                await ScheduleRetryAsync(route, "SourceTemporarilyUnavailable",
                    "The saved source became temporarily unavailable; standby is active and the known RTSP URI will be retried without waiting for discovery.",
                    cancellationToken);
                return;
            }
            if (!active && (route.State is RouteState.Starting or RouteState.Running or RouteState.Reconnecting or RouteState.Fallback))
            {
                if (_supervisor is not null) await _supervisor.StopAsync(source.Identity, cancellationToken);
            }
            var shouldReserve = active || DesiredRoutePolicy.ProtectsPortWhileOffline(route);
            var identity = source.Identity;
            if (shouldReserve && !_reservations.TryReserve(port.StableId, identity, route.Locked, DateTimeOffset.UtcNow, out var existing))
            {
                var ownerRoute = RoutesCopy().FirstOrDefault(value => value.SourceId == existing.Source.Value);
                if (active && ownerRoute is not null
                    && DesiredRoutePolicy.PriorityRank(route.AssignmentMode) > DesiredRoutePolicy.PriorityRank(ownerRoute.AssignmentMode))
                {
                    if (DesiredRoutePolicy.HasSavedAssignment(ownerRoute))
                        await YieldSavedRouteAsync(ownerRoute, cancellationToken);
                    else
                        await StopRouteAsync(ownerRoute.SourceId, forceRelease: true, cancellationToken, preserveDesiredAssignment: false);
                    _reservations.TryReserve(port.StableId, identity, route.Locked, DateTimeOffset.UtcNow, out existing);
                }
                var ownsDesiredPort = _reservations.Snapshot().Any(value =>
                    value.PortId.Equals(port.StableId, StringComparison.OrdinalIgnoreCase)
                    && value.Source.Value == sourceId);
                if (!ownsDesiredPort)
                {
                    await ReplaceRouteAsync(route with
                    {
                        State = RouteState.WaitingForPort,
                        PortId = null,
                        PortName = null,
                        FailureCategory = "RoutingConflict",
                        FailureMessage = $"Saved output is currently owned by {existing.Source.Value}.",
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, route.State, cancellationToken);
                    return;
                }
            }

            if (!shouldReserve && route.PortId is not null)
                _reservations.Release(route.PortId, identity, force: true);

            // A reconnecting saved route may currently own its generated fallback.
            // Reap that exact owner before the live FFmpeg process is started; otherwise
            // the supervisor correctly rejects the replacement as duplicate ownership.
            if (active && ownedProcess is not null && _supervisor is not null)
                await _supervisor.StopForOutputHandoffAsync(identity, cancellationToken);

            var assigned = route with
            {
                PortId = shouldReserve ? port.StableId : null,
                PortName = shouldReserve ? DeckLinkDisplayName.Full(port) : null,
                State = active ? RouteState.Reserved : RouteState.WaitingForStream,
                FailureCategory = null,
                FailureMessage = active ? null : "Waiting for the incoming stream to become active.",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await ReplaceRouteAsync(assigned, route.State, cancellationToken);
            if (!active) return;
            if (!_settings.SimulationMode && !_validation.CanStartHardwareRoutes)
                throw new InvalidOperationException("DeckLink route start refused because Media Tools validation has not passed.");
            await StartRouteWithRecoveryAsync(assigned, source, port, preset, cancellationToken);
        }
        finally { routeGate.Release(); }
    }

    private async Task RestartRoutesForMediaModeChangesAsync(CancellationToken cancellationToken)
    {
        string[] pending;
        lock (_gate) pending = _pendingMediaModeRestarts.ToArray();
        foreach (var sourceId in pending)
        {
            var routeGate = _routeGates.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1));
            await routeGate.WaitAsync(cancellationToken);
            try
            {
                RuntimeRoute? route;
                DiscoveredSource? source;
                DeckLinkPort? port;
                OutputPresetProfile? preset;
                lock (_gate)
                {
                    _routes.TryGetValue(sourceId, out route);
                    _sources.TryGetValue(sourceId, out source);
                    port = route?.PortId is null ? null : _ports.GetValueOrDefault(route.PortId);
                    preset = route is null ? null : _settings.Presets.FirstOrDefault(value => value.Id == route.PresetId);
                }

                if (route is null || source?.State != SourceState.Ready || port is null || preset is null
                    || route.State is not (RouteState.Starting or RouteState.Running or RouteState.Reconnecting or RouteState.Fallback))
                {
                    lock (_gate) _pendingMediaModeRestarts.Remove(sourceId);
                    continue;
                }
                if (route.RetryAt is not null && route.RetryAt > DateTimeOffset.UtcNow) continue;

                if (_supervisor is not null) await _supervisor.StopAsync(source.Identity, cancellationToken);
                var restarting = route with
                {
                    State = RouteState.Reconnecting,
                    FailureCategory = "MediaModeChanged",
                    FailureMessage = source.Media?.HasUsableVideo == true
                        ? "Stable decoded video was confirmed; restarting playout in live-video mode."
                        : "Sustained audio-led input was confirmed; restarting playout with generated black video.",
                    RetryAt = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await ReplaceRouteAsync(restarting, route.State, cancellationToken);
                lock (_gate) _pendingMediaModeRestarts.Remove(sourceId);
                await LogAsync("Information", "MediaMode",
                    restarting.FailureMessage, sourceId, cancellationToken: cancellationToken);
                await StartRouteWithRecoveryAsync(restarting, source, port, preset, cancellationToken);
            }
            finally { routeGate.Release(); }
        }
    }

    private async Task StartRouteWithRecoveryAsync(RuntimeRoute route, DiscoveredSource source, DeckLinkPort port,
        OutputPresetProfile preset, CancellationToken cancellationToken)
    {
        try
        {
            await StartRouteAsync(route, source, port, preset, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeRoute current;
            lock (_gate) current = _routes.GetValueOrDefault(route.SourceId) ?? route;
            var detail = LogRedactor.Redact(ex.Message);
            var category = FfmpegErrorClassifier.Classify(null, detail);
            if (IsPermanent(category))
            {
                await ReplaceRouteAsync(current with
                {
                    State = RouteState.Failed,
                    FailureCategory = category.ToString(),
                    FailureMessage = detail,
                    RetryAt = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, current.State, cancellationToken);
            }
            else
            {
                await ScheduleRetryAsync(current, category.ToString(), detail, cancellationToken);
            }
        }
    }

    private async Task RemoveDesiredRouteAsync(string sourceId, string actor, CancellationToken cancellationToken)
    {
        await StopRouteAsync(sourceId, forceRelease: true, cancellationToken, preserveDesiredAssignment: false);
        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        if (route is null) return;
        var removed = route with
        {
            AssignmentMode = AssignmentMode.None,
            DesiredPortId = null,
            DesiredPortName = null,
            ReserveWhileOffline = false,
            AllowTemporaryUse = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await ReplaceRouteAsync(removed, route.State, cancellationToken);
        await store.WriteConfigurationAuditAsync(new(0, DateTimeOffset.UtcNow, "RoutingAssignment", route.DesiredPortId ?? "",
            "", route.DesiredPortName ?? "", DescribeRouteAssignment(route), DescribeRouteAssignment(removed), actor,
            "Saved routing assignment removed", "Persisted and applied", sourceId), cancellationToken);
    }

    private async Task YieldSavedRouteAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        var ownerGate = _routeGates.GetOrAdd(route.SourceId, static _ => new SemaphoreSlim(1, 1));
        await ownerGate.WaitAsync(cancellationToken);
        try
        {
            var identity = SourceIdentityFromValue(route.SourceId);
            if (_supervisor is not null) await _supervisor.StopAsync(identity, cancellationToken);
            if (route.PortId is not null) _reservations.Release(route.PortId, identity, force: true);
            await ReplaceRouteAsync(route with
            {
                PortId = null,
                PortName = null,
                State = RouteState.WaitingForPort,
                FailureCategory = "RoutingConflict",
                FailureMessage = "A higher-priority saved routing entry owns this output.",
                UpdatedAt = DateTimeOffset.UtcNow
            }, route.State, cancellationToken);
        }
        finally { ownerGate.Release(); }
    }

    private async Task EnsureRouteAsync(string sourceId, string? requestedPortId, string? requestedPresetId, bool manual,
        CancellationToken cancellationToken)
    {
        var routeGate = _routeGates.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1));
        await routeGate.WaitAsync(cancellationToken);
        try
        {
            RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
            DiscoveredSource source;
            DeckLinkPort[] ports;
            OperatorSettings settings;
            RuntimeRoute? previousRoute;
            lock (_gate)
            {
                if (!_sources.TryGetValue(sourceId, out source!)) throw new InvalidOperationException("Source is not currently known.");
                var protectedPorts = _routes.Values.Where(route => route.SourceId != sourceId && DesiredRoutePolicy.ProtectsPortWhileOffline(route))
                    .Select(route => route.DesiredPortId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                ports = _ports.Values.Where(port => !protectedPorts.Contains(port.StableId)).ToArray();
                settings = Clone(_settings);
                _routes.TryGetValue(sourceId, out previousRoute);
            }
            if (DesiredRoutePolicy.HasSavedAssignment(previousRoute)) return;
            if (previousRoute?.State is RouteState.Reserved or RouteState.Starting or RouteState.Running or RouteState.Fallback) return;
            if (!settings.SimulationMode && !_validation.CanStartHardwareRoutes)
                throw new InvalidOperationException("DeckLink route start refused because Media Tools validation has not passed.");
            var defaultPreset = settings.Presets.FirstOrDefault()?.Id ?? "";
            var decision = RoutingRuleEvaluator.Evaluate(source, settings.Rules, defaultPreset);
            var effective = source with
            {
                FixedPortId = requestedPortId ?? decision.FixedPortId,
                AssignmentLocked = decision.Locked,
                Priority = decision.Priority,
                AutomaticRoutingEnabled = true
            };
            var presetProfile = OutputPresetSelection.Resolve(settings.Presets, decision.PresetId,
                manual ? requestedPresetId : null);
            var assignment = new AutomaticAssignmentEngine(_reservations, _waiting).Assign(effective, ports,
                port => Compatible(port, presetProfile, decision.PortGroup));
            if (!assignment.Assigned)
            {
                var waiting = new RuntimeRoute(sourceId, source.FriendlyName, null, null, presetProfile.Id, RouteState.WaitingForPort,
                    assignment.Mode, decision.Locked, decision.Priority, 0, null, null, null, 0, 0, null, DateTimeOffset.UtcNow,
                    "NoOutput", assignment.Reason);
                await ReplaceRouteAsync(waiting, previousRoute?.State, cancellationToken);
                return;
            }

            var port = assignment.Port!;
            _waiting.Remove(source.Identity);
            var now = DateTimeOffset.UtcNow;
            var route = new RuntimeRoute(sourceId, source.FriendlyName, port.StableId, DeckLinkDisplayName.Full(port), presetProfile.Id,
                RouteState.Reserved, manual ? AssignmentMode.Manual : assignment.Mode, decision.Locked, decision.Priority, 0,
                null, null, null, 0, 0, null, now, null, null);
            await ReplaceRouteAsync(route, previousRoute?.State, cancellationToken);
            try
            {
                await StartRouteAsync(route, source, port, presetProfile, cancellationToken);
            }
            catch (Exception ex)
            {
                ClearCutoverMeasurement(sourceId);
                var detail = LogRedactor.Redact(ex.Message);
                var failed = RouteStartFailureRecovery.ReleaseAndFail(_reservations, route with { State = RouteState.Starting }, source.Identity,
                    string.IsNullOrWhiteSpace(detail) ? "FFmpeg could not be started." : detail, DateTimeOffset.UtcNow);
                await ReplaceRouteAsync(failed, RouteState.Starting, cancellationToken);
                throw new InvalidOperationException("FFmpeg failed to start; the output reservation was released.", ex);
            }
        }
        finally { routeGate.Release(); }
    }

    private async Task StartRouteAsync(RuntimeRoute route, DiscoveredSource source, DeckLinkPort port, OutputPresetProfile preset, CancellationToken cancellationToken)
    {
        var portGate = _portGates.GetOrAdd(port.StableId, static _ => new SemaphoreSlim(1, 1));
        await portGate.WaitAsync(cancellationToken);
        try
        {
            var standbyOwner = StandbyIdentity(port.StableId);
            var standbyWasRunning = _supervisor?.Snapshot().Any(value => value.Source == standbyOwner && value.Running) == true;
            if (standbyWasRunning)
            {
                lock (_gate) _cutoverStartedAt[route.SourceId] = DateTimeOffset.UtcNow;
                await StopPortStandbyForHandoffAsync(port.StableId, cancellationToken);
            }
            else
            {
                await StopPortStandbyAsync(port.StableId, cancellationToken);
            }
            var starting = route with { State = RouteState.Starting, UpdatedAt = DateTimeOffset.UtcNow, FailureCategory = null, FailureMessage = null, RetryAt = null };
            await ReplaceRouteAsync(starting, route.State, cancellationToken);
            if (_settings.SimulationMode)
            {
                ClearCutoverMeasurement(route.SourceId);
                await ReplaceRouteAsync(starting with { State = RouteState.Running, StartedAt = DateTimeOffset.UtcNow, Frame = 0, Fps = preset.FrameRateNumerator / (double)preset.FrameRateDenominator, Speed = 1 }, RouteState.Starting, cancellationToken);
                return;
            }

            EnsureSupervisor(_settings.MediaTools.FfmpegPath, _validation.WindowsDeckLinkSafeTerminateSupported);
            var domainRoute = new RouteRecord(source.Identity, port.StableId, preset.Id, RouteState.Starting, starting.AssignmentMode, starting.Locked, starting.RestartCount);
            await _supervisor!.StartAsync(domainRoute, source, port, preset.ToDomain(), cancellationToken);
            if (_emergencyStopped)
            {
                await _supervisor.StopAsync(source.Identity, CancellationToken.None);
                throw new InvalidOperationException("Emergency stop became active while FFmpeg was starting.");
            }
        }
        finally { portGate.Release(); }
    }

    private async Task ReconcilePortStandbysAsync(CancellationToken cancellationToken)
    {
        if (_emergencyStopped) return;
        DeckLinkPort[] ports;
        RuntimeRoute[] routes;
        OperatorSettings settings;
        lock (_gate)
        {
            ports = _ports.Values.ToArray();
            routes = _routes.Values.ToArray();
            settings = Clone(_settings);
        }

        var outputIds = ports.Where(port => port.IsOutputPort).Select(port => port.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] staleStandbys;
        lock (_gate) staleStandbys = _standbys.Keys.Where(id => !outputIds.Contains(id)).ToArray();
        foreach (var stale in staleStandbys)
            await StopPortStandbyAsync(stale, cancellationToken);

        foreach (var port in ports.Where(port => port.IsOutputPort))
        {
            var activeRoute = routes.FirstOrDefault(route => string.Equals(route.PortId, port.StableId, StringComparison.OrdinalIgnoreCase)
                && route.State is RouteState.Starting or RouteState.Running or RouteState.Reconnecting or RouteState.Fallback);
            if (activeRoute is not null)
            {
                await StopPortStandbyAsync(port.StableId, cancellationToken);
                SetStandbyStatus(port.StableId, PortStandbyState.Live, null, "Live route owns output", null);
                continue;
            }

            var configuration = settings.DeckLinkPortOverrides.FirstOrDefault(value =>
                value.StableId.Equals(port.StableId, StringComparison.OrdinalIgnoreCase));
            if (configuration is null || !configuration.StandbyEnabled)
            {
                await StopPortStandbyAsync(port.StableId, cancellationToken);
                SetStandbyStatus(port.StableId, PortStandbyState.Disabled, null, "Standby disabled", null);
                continue;
            }
            var preset = settings.Presets.FirstOrDefault(value => value.Id == configuration.StandbyPresetId);
            if (preset is null)
            {
                SetStandbyStatus(port.StableId, PortStandbyState.Failed, null, "Standby configuration error",
                    "Select a valid standby output preset.");
                continue;
            }
            var desiredSignature = StandbyConfigurationSignature(port, preset, configuration);
            if (settings.SimulationMode)
            {
                SetStandbyStatus(port.StableId, PortStandbyState.Running, null, "Simulated standby screen", null);
                continue;
            }
            if (!_validation.CanStartHardwareRoutes || !_validation.PortStandbySupported)
            {
                SetStandbyStatus(port.StableId, PortStandbyState.Failed, null, "Standby blocked",
                    "Media Tools validation or required standby filters have not passed.");
                continue;
            }
            if (_standbyRetryAt.TryGetValue(port.StableId, out var retryAt) && retryAt > DateTimeOffset.UtcNow)
                continue;

            var owner = StandbyIdentity(port.StableId);
            var process = _supervisor?.Snapshot().FirstOrDefault(value => value.Source == owner && value.Running);
            if (process is not null)
            {
                string? appliedSignature;
                lock (_gate) _standbyConfigurationSignatures.TryGetValue(port.StableId, out appliedSignature);
                if (string.Equals(appliedSignature, desiredSignature, StringComparison.Ordinal))
                {
                    SetStandbyStatus(port.StableId, PortStandbyState.Running, process.ProcessId, "Standby screen on air", null);
                    continue;
                }
            }

            var portGate = _portGates.GetOrAdd(port.StableId, static _ => new SemaphoreSlim(1, 1));
            if (!await portGate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                lock (_gate)
                    activeRoute = _routes.Values.FirstOrDefault(route => string.Equals(route.PortId, port.StableId, StringComparison.OrdinalIgnoreCase)
                        && route.State is RouteState.Starting or RouteState.Running or RouteState.Reconnecting or RouteState.Fallback);
                if (activeRoute is not null) continue;

                // Re-read process ownership after acquiring the port gate. Settings saves,
                // the one-second reconciler, and a live cutover can all reach this path.
                // Stopping outside the gate allowed a replacement to start while the old
                // FFmpeg child was still releasing DeckLink audio/video resources.
                process = _supervisor?.Snapshot().FirstOrDefault(value => value.Source == owner && value.Running);
                if (process is not null)
                {
                    string? appliedSignature;
                    lock (_gate) _standbyConfigurationSignatures.TryGetValue(port.StableId, out appliedSignature);
                    if (string.Equals(appliedSignature, desiredSignature, StringComparison.Ordinal))
                    {
                        SetStandbyStatus(port.StableId, PortStandbyState.Running, process.ProcessId, "Standby screen on air", null);
                        continue;
                    }

                    await _supervisor!.StopAsync(owner, cancellationToken);
                    ClearStandbyState(port.StableId);
                    await LogAsync("Information", "PortStandby",
                        $"Standby configuration changed for {DeckLinkDisplayName.Full(port)}; owned process {process.ProcessId} exited before replacement.",
                        cancellationToken: cancellationToken);
                }
                EnsureSupervisor(settings.MediaTools.FfmpegPath, _validation.WindowsDeckLinkSafeTerminateSupported);
                SetStandbyStatus(port.StableId, PortStandbyState.Starting, null, "Starting standby screen", null);
                try
                {
                    await _supervisor!.StartPortStandbyAsync(owner, port, preset.ToDomain(),
                        new PortStandbyConfiguration(configuration.StandbyPattern,
                            EmptyToNull(configuration.StandbyLogoPath), configuration.StandbyLabel, configuration.StandbyShowClock),
                        cancellationToken);
                    var started = _supervisor.Snapshot().FirstOrDefault(value => value.Source == owner && value.Running);
                    _standbyRetryAt.Remove(port.StableId);
                    lock (_gate) _standbyConfigurationSignatures[port.StableId] = desiredSignature;
                    SetStandbyStatus(port.StableId, PortStandbyState.Running, started?.ProcessId, "Standby screen on air", null);
                }
                catch (Exception ex)
                {
                    _standbyRetryAt[port.StableId] = DateTimeOffset.UtcNow.AddSeconds(10);
                    SetStandbyStatus(port.StableId, PortStandbyState.Failed, null, "Standby start failed",
                        LogRedactor.Redact(ex.Message));
                    await LogAsync("Warning", "PortStandby",
                        $"Standby screen could not start for {DeckLinkDisplayName.Full(port)}: {ex.Message}",
                        cancellationToken: cancellationToken);
                }
            }
            finally { portGate.Release(); }
        }
    }

    private async Task StopPortStandbyAsync(string portId, CancellationToken cancellationToken)
    {
        if (_supervisor is not null)
            await _supervisor.StopAsync(StandbyIdentity(portId), cancellationToken);
        ClearStandbyState(portId);
    }

    private async Task StopPortStandbyForHandoffAsync(string portId, CancellationToken cancellationToken)
    {
        if (_supervisor is not null)
            await _supervisor.StopForOutputHandoffAsync(StandbyIdentity(portId), cancellationToken);
        ClearStandbyState(portId);
    }

    private void ClearStandbyState(string portId)
    {
        lock (_gate)
        {
            _standbys.Remove(portId);
            _standbyConfigurationSignatures.Remove(portId);
        }
    }

    private void ClearCutoverMeasurement(string sourceId)
    {
        lock (_gate) _cutoverStartedAt.Remove(sourceId);
    }

    private async Task StopAllPortStandbysAsync(CancellationToken cancellationToken)
    {
        string[] portIds;
        lock (_gate) portIds = _standbys.Keys.ToArray();
        foreach (var portId in portIds) await StopPortStandbyAsync(portId, cancellationToken);
    }

    private void SetStandbyStatus(string portId, PortStandbyState state, int? processId, string summary, string? error)
    {
        lock (_gate)
            _standbys[portId] = new(portId, state, processId, summary, error, DateTimeOffset.UtcNow);
    }

    private static SourceIdentity StandbyIdentity(string portId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(portId)))[..24];
        return new SourceIdentity("SYSTEM", "port-standby", "_definst_", hash);
    }

    private static string StandbyConfigurationSignature(DeckLinkPort port, OutputPresetProfile preset,
        DeckLinkPortOverride configuration)
    {
        var value = JsonSerializer.Serialize(new
        {
            port.StableId,
            port.FfmpegName,
            port.FriendlyName,
            port.CardFriendlyName,
            Preset = preset,
            configuration.StandbyEnabled,
            configuration.StandbyPattern,
            configuration.StandbyLogoPath,
            configuration.StandbyLabel,
            configuration.StandbyShowClock
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private async Task MonitorProcessesAsync(CancellationToken cancellationToken)
    {
        await RefreshDeckLinkReferenceStatusAsync(cancellationToken);
        if (_settings.SimulationMode)
        {
            foreach (var route in RoutesCopy().Where(x => x.State == RouteState.Running))
            {
                if (_simulationFaults.TryGetValue(route.SourceId, out var fault))
                {
                    var permanent = fault == "Configuration";
                    var failed = route with
                    {
                        State = permanent ? RouteState.Failed : RouteState.Reconnecting,
                        RestartCount = route.RestartCount + 1,
                        FailureCategory = fault,
                        FailureMessage = $"Simulated {fault} injected.",
                        RetryAt = permanent ? null : DateTimeOffset.UtcNow + RetryDelay(route.RestartCount + 1),
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await ReplaceRouteAsync(failed, route.State, cancellationToken);
                    continue;
                }
                var elapsed = DateTimeOffset.UtcNow - (route.StartedAt ?? DateTimeOffset.UtcNow);
                var fps = route.Fps ?? 25;
                await ReplaceRouteAsync(route with { Frame = (long)(elapsed.TotalSeconds * fps), Speed = 1, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken, persistHistory: false);
            }
            return;
        }

        if (_supervisor is null) return;
        foreach (var process in _supervisor.Snapshot())
        {
            try
            {
                var routeGate = _routeGates.GetOrAdd(process.Source.Value, static _ => new SemaphoreSlim(1, 1));
                await routeGate.WaitAsync(cancellationToken);
                try
                {
                RuntimeRoute? route;
                lock (_gate) _routes.TryGetValue(process.Source.Value, out route);
                if (route is null) continue;
                var progress = process.Progress;
                var now = DateTimeOffset.UtcNow;
            if (FfmpegStallDetector.IsFirstProgressTimedOut(process.Running, progress, process.StartedAt, now,
                    TimeSpan.FromSeconds(_settings.Routing.FirstProgressTimeoutSeconds)))
            {
                await _supervisor.StopAsync(process.Source, cancellationToken);
                await ScheduleRetryAsync(route, "NoFirstProgress", "FFmpeg started but produced no progress before the startup deadline.", cancellationToken);
            }
            else if (process.Purpose == RouteProcessPurpose.Live
                     && process.Running && FfmpegStallDetector.IsStalled(true, progress, now, TimeSpan.FromSeconds(_settings.Routing.StallTimeoutSeconds)))
            {
                await _supervisor.StopAsync(process.Source, cancellationToken);
                await ScheduleRetryAsync(route, "VideoStalled", "FFmpeg remained alive but stopped producing progress.", cancellationToken);
            }
            else if (process.Purpose == RouteProcessPurpose.Live && process.Running && IsInputFrozen(process, now))
            {
                await _supervisor.StopAsync(process.Source, cancellationToken);
                await LogAsync("Warning", "InputLiveness",
                    $"FFmpeg process {process.ProcessId} kept advancing with duplicate-dominated video; the decoder session will be recreated.",
                    route.SourceId, cancellationToken: cancellationToken);
                await ScheduleRetryAsync(route, "InputFrozen",
                    "FFmpeg output remained active but almost every new frame was duplicated, indicating a stale input decoder session.",
                    cancellationToken);
            }
            else if (process.Running)
            {
                var state = process.Purpose == RouteProcessPurpose.Fallback
                    ? RouteState.Fallback
                    : progress?.Frame > 0 ? RouteState.Running : RouteState.Starting;
                if (state == RouteState.Running && route.State is not (RouteState.Starting or RouteState.Running))
                {
                    var starting = route with { State = RouteState.Starting, UpdatedAt = DateTimeOffset.UtcNow };
                    await ReplaceRouteAsync(starting, route.State, cancellationToken);
                    route = starting;
                }
                DateTimeOffset? cutoverStarted = null;
                if (state == RouteState.Running && route.State != RouteState.Running)
                {
                    lock (_gate)
                    {
                        if (_cutoverStartedAt.Remove(route.SourceId, out var startedAt)) cutoverStarted = startedAt;
                    }
                }
                var processDetail = string.Join(" | ", process.RecentErrors.TakeLast(5));
                var outputCategory = FfmpegErrorClassifier.Classify(null, processDetail);
                var outputDiagnostic = outputCategory is FfmpegFailureCategory.DeckLinkInitialization
                    or FfmpegFailureCategory.DeckLinkReference or FfmpegFailureCategory.DeckLinkBusy
                    or FfmpegFailureCategory.DeckLinkUnavailable or FfmpegFailureCategory.DeckLinkFormat;
                if (outputDiagnostic)
                {
                    var signature = $"{process.ProcessId}:{outputCategory}:{processDetail}";
                    var shouldLog = false;
                    lock (_gate)
                    {
                        if (!_processDiagnosticSignatures.TryGetValue(route.SourceId, out var previousSignature)
                            || !previousSignature.Equals(signature, StringComparison.Ordinal))
                        {
                            _processDiagnosticSignatures[route.SourceId] = signature;
                            shouldLog = true;
                        }
                    }
                    if (shouldLog)
                        await LogAsync("Warning", "DeckLinkOutput",
                            $"{outputCategory}: FFmpeg process {process.ProcessId} reported an output-path warning while processing frames. {processDetail}",
                            route.SourceId, cancellationToken: cancellationToken);
                }
                await ReplaceRouteAsync(route with
                {
                    State = state,
                    Frame = progress?.Frame,
                    Fps = progress?.Fps,
                    Speed = progress?.Speed,
                    DroppedFrames = progress?.DroppedFrames ?? 0,
                    DuplicatedFrames = progress?.DuplicatedFrames ?? 0,
                    RestartCount = state == RouteState.Running ? 0 : route.RestartCount,
                    StartedAt = process.StartedAt,
                    FailureCategory = outputDiagnostic ? outputCategory.ToString() :
                        route.FailureCategory?.StartsWith("DeckLink", StringComparison.Ordinal) == true ? null : route.FailureCategory,
                    FailureMessage = outputDiagnostic ? processDetail :
                        route.FailureCategory?.StartsWith("DeckLink", StringComparison.Ordinal) == true ? null : route.FailureMessage,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken, persistHistory: state != route.State);
                if (cutoverStarted is not null)
                {
                    var elapsed = DateTimeOffset.UtcNow - cutoverStarted.Value;
                    await LogAsync("Information", "Cutover",
                        $"Standby-to-live cutover produced its first DeckLink frame in {elapsed.TotalMilliseconds:F0} ms.",
                        route.SourceId, cancellationToken: cancellationToken);
                }
            }
            else if (route.State is RouteState.Starting or RouteState.Running or RouteState.Fallback)
            {
                var detail = string.Join(" | ", process.RecentErrors.TakeLast(5));
                var category = FfmpegErrorClassifier.Classify(process.ExitCode, detail);
                if (IsPermanent(category))
                    await ReplaceRouteAsync(route with { State = RouteState.Failed, FailureCategory = category.ToString(), FailureMessage = detail, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
                else await ScheduleRetryAsync(route, category.ToString(), detail, cancellationToken);
            }
                }
                finally { routeGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await LogAsync("Error", "ProcessSupervision",
                    $"Owned FFmpeg process {process.ProcessId} could not be reconciled; other routes will continue. {LogRedactor.Redact(ex.Message)}",
                    process.Source.Value, cancellationToken: cancellationToken);
            }
        }
    }

    private bool IsInputFrozen(RouteProcessSnapshot process, DateTimeOffset now)
    {
        DiscoveredSource? source;
        lock (_gate) _sources.TryGetValue(process.Source.Value, out source);
        if (source?.Media?.HasUsableVideo != true)
        {
            lock (_gate) _inputFreezeDetectors.Remove(process.Source.Value);
            return false;
        }

        FfmpegInputFreezeDetector detector;
        lock (_gate)
        {
            if (!_inputFreezeDetectors.TryGetValue(process.Source.Value, out detector!))
            {
                detector = new FfmpegInputFreezeDetector();
                _inputFreezeDetectors[process.Source.Value] = detector;
            }
        }
        var allowRapidBurst = source.Media.FramesPerSecond is >= 10;
        return detector.Observe(process.ProcessId, process.Progress, now,
            TimeSpan.FromSeconds(_settings.Routing.FrozenInputTimeoutSeconds), allowRapidBurst);
    }

    private async Task ScheduleRetryAsync(RuntimeRoute route, string category, string detail, CancellationToken cancellationToken)
    {
        var count = route.RestartCount + 1;
        if (!DesiredRoutePolicy.HasSavedAssignment(route)
            && RetryLimitPolicy.IsExhausted(count, _settings.Routing.MaxRetryAttempts))
        {
            var terminal = route with
            {
                State = RouteState.Failed,
                RestartCount = count,
                FailureCategory = "RetryLimitExceeded",
                FailureMessage = $"Retry limit reached after {_settings.Routing.MaxRetryAttempts} attempt(s). Last failure: {detail}",
                RetryAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            if (!terminal.Locked && terminal.PortId is not null)
            {
                _reservations.Release(terminal.PortId, SourceIdentityFromValue(terminal.SourceId), force: false);
                terminal = terminal with { PortId = null, PortName = null };
            }
            await ReplaceRouteAsync(terminal, route.State, cancellationToken);
            return;
        }
        var retry = route with
        {
            State = RouteState.Reconnecting,
            RestartCount = count,
            FailureCategory = category,
            FailureMessage = string.IsNullOrWhiteSpace(detail) ? "FFmpeg stopped unexpectedly." : detail,
            RetryAt = DateTimeOffset.UtcNow + RetryDelay(count),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await LogAsync("Warning", "FFmpeg", $"Route process failed ({category}); retry {count} is scheduled. {retry.FailureMessage}",
            route.SourceId, cancellationToken: cancellationToken);
        if (!_settings.SimulationMode && route.PortId is not null)
        {
            try
            {
                await StartFallbackAsync(retry, cancellationToken);
                retry = retry with { State = RouteState.Fallback };
            }
            catch (Exception ex)
            {
                await LogAsync("Warning", "Fallback", $"Fallback output could not start: {ex.Message}", route.SourceId, cancellationToken: cancellationToken);
            }
        }
        await ReplaceRouteAsync(retry, route.State, cancellationToken);
    }

    private async Task RestartReservedRouteAsync(RuntimeRoute route, CancellationToken cancellationToken, bool force = false)
    {
        var routeGate = _routeGates.GetOrAdd(route.SourceId, static _ => new SemaphoreSlim(1, 1));
        await routeGate.WaitAsync(cancellationToken);
        try
        {
            RuntimeRoute? current;
            DiscoveredSource? source;
            DeckLinkPort? port;
            OutputPresetProfile? preset;
            lock (_gate)
            {
                _routes.TryGetValue(route.SourceId, out current);
                _sources.TryGetValue(route.SourceId, out source);
                port = current?.PortId is null ? null : _ports.GetValueOrDefault(current.PortId);
                preset = current is null ? null : _settings.Presets.FirstOrDefault(x => x.Id == current.PresetId);
            }
            if (current?.PortId is null || source is null || port is null || preset is null) return;
            if (!force && (current.State is not (RouteState.Reconnecting or RouteState.Fallback)
                || current.RetryAt is null || current.RetryAt > DateTimeOffset.UtcNow)) return;
            if (_simulationFaults.ContainsKey(current.SourceId)) return;
            if (_supervisor is not null && current.State == RouteState.Fallback)
                await _supervisor.StopForOutputHandoffAsync(source.Identity, cancellationToken);
            await StartRouteWithRecoveryAsync(current, source, port, preset, cancellationToken);
        }
        finally { routeGate.Release(); }
    }

    private async Task StartFallbackAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        if (route.PortId is null) return;
        DeckLinkPort? port;
        OutputPresetProfile? preset;
        DeckLinkPortOverride? portConfiguration;
        lock (_gate)
        {
            _ports.TryGetValue(route.PortId, out port);
            preset = _settings.Presets.FirstOrDefault(x => x.Id == route.PresetId);
            portConfiguration = _settings.DeckLinkPortOverrides.FirstOrDefault(value =>
                value.StableId.Equals(route.PortId, StringComparison.OrdinalIgnoreCase));
        }
        if (port is null || preset is null) throw new InvalidOperationException("The reserved port or output preset is unavailable.");
        EnsureSupervisor(_settings.MediaTools.FfmpegPath, _validation.WindowsDeckLinkSafeTerminateSupported);
        if (portConfiguration?.StandbyEnabled == true)
        {
            await _supervisor!.StartRecoveryStandbyAsync(SourceIdentityFromValue(route.SourceId), port, preset.ToDomain(),
                new PortStandbyConfiguration(portConfiguration.StandbyPattern,
                    EmptyToNull(portConfiguration.StandbyLogoPath), portConfiguration.StandbyLabel,
                    portConfiguration.StandbyShowClock), cancellationToken);
        }
        else
        {
            await _supervisor!.StartFallbackAsync(SourceIdentityFromValue(route.SourceId), port, preset.ToDomain(),
                preset.StandbyMode, preset.StandbyValue, cancellationToken);
        }
    }

    private async Task StopRouteAsync(string sourceId, bool forceRelease, CancellationToken cancellationToken,
        bool preserveDesiredAssignment = true)
    {
        var routeGate = _routeGates.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1));
        await routeGate.WaitAsync(cancellationToken);
        try
        {
            RuntimeRoute? route;
            lock (_gate) _routes.TryGetValue(sourceId, out route);
            if (route is null) return;
            var preserve = preserveDesiredAssignment && DesiredRoutePolicy.HasSavedAssignment(route);
            var keepReservation = preserve && DesiredRoutePolicy.ProtectsPortWhileOffline(route);
            if (!keepReservation) RouteControlSafety.EnsureStopAllowed(route.Locked, forceRelease);
            var identity = SourceIdentityFromValue(sourceId);
            _waiting.Remove(identity);
            lock (_gate)
            {
                _sourceMissingSince.Remove(sourceId);
                _sourceReadySince.Remove(sourceId);
                _cutoverStartedAt.Remove(sourceId);
            }
            var portGate = route.PortId is null
                ? null
                : _portGates.GetOrAdd(route.PortId, static _ => new SemaphoreSlim(1, 1));
            if (portGate is not null) await portGate.WaitAsync(cancellationToken);
            try
            {
                if (_supervisor is not null) await _supervisor.StopAsync(identity, cancellationToken);
                if (route.PortId is not null && !keepReservation)
                {
                    var release = _reservations.ReleaseWithResult(route.PortId, identity, forceRelease);
                    if (release == PortReleaseResult.OwnedByOther)
                        throw new InvalidOperationException("The output reservation could not be released because another route now owns it.");
                    if (release == PortReleaseResult.Locked)
                        throw new InvalidOperationException("The output reservation is locked and requires a forced stop.");
                    if (release == PortReleaseResult.AlreadyFree)
                        await LogAsync("Warning", "Reservation", "A stale route referenced an output that was already free; stop reconciled the route without releasing another owner's lease.",
                            sourceId, cancellationToken: cancellationToken);
                }
                await ReplaceRouteAsync(route with
                {
                    State = preserve ? RouteState.WaitingForStream : RouteState.Released,
                    PortId = keepReservation ? route.PortId : null,
                    PortName = keepReservation ? route.PortName : null,
                    DesiredPortId = preserve ? route.DesiredPortId : null,
                    DesiredPortName = preserve ? route.DesiredPortName : null,
                    AssignmentMode = preserve ? route.AssignmentMode : AssignmentMode.None,
                    FailureCategory = null,
                    FailureMessage = preserve ? "Waiting for the incoming stream to become active." : null,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken);
            }
            finally { portGate?.Release(); }
        }
        finally { routeGate.Release(); }
    }

    private async Task ReleaseExpiredMissingRoutesAsync(OperatorSettings settings, CancellationToken cancellationToken)
    {
        KeyValuePair<string, DateTimeOffset>[] missing;
        lock (_gate) missing = _sourceMissingSince.ToArray();
        var now = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Max(0, settings.Routing.ReservationGraceSeconds));
        foreach (var item in missing)
        {
            RuntimeRoute? route;
            lock (_gate) _routes.TryGetValue(item.Key, out route);
            if (route is null || route.State is RouteState.Released or RouteState.Disabled)
            {
                lock (_gate) _sourceMissingSince.Remove(item.Key);
                continue;
            }
            if (!RouteLeaseRetentionPolicy.ShouldRelease(route.Locked, item.Value, now, grace)) continue;
            await StopRouteAsync(item.Key, forceRelease: false, cancellationToken);
            await LogAsync("Warning", "Discovery", "Source remained absent beyond its reservation grace period; its unlocked output was released.",
                item.Key, cancellationToken: cancellationToken);
        }
    }

    private bool SourceHasBeenReadyLongEnough(string sourceId, int stableSeconds)
    {
        lock (_gate)
            return _sourceReadySince.TryGetValue(sourceId, out var readySince)
                   && RouteLeaseRetentionPolicy.IsStable(readySince, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(Math.Max(0, stableSeconds)));
    }

    private async Task RestoreReservationsAndProcessesAsync(OperatorSettings settings, CancellationToken cancellationToken)
    {
        var candidates = RoutesCopy().Where(x => !DesiredRoutePolicy.HasSavedAssignment(x)
                && x.PortId is not null && x.State is not RouteState.Released and not RouteState.Disabled)
            .OrderByDescending(x => x.Locked).ThenByDescending(x => x.Priority).ToArray();
        foreach (var route in candidates)
        {
            if (!_startupRecovery.IsPending(route.SourceId)) continue;
            DiscoveredSource? source;
            DeckLinkPort? port;
            lock (_gate)
            {
                _sources.TryGetValue(route.SourceId, out source);
                _ports.TryGetValue(route.PortId!, out port);
            }
            if (source is null || port is null) continue;
            if (!_reservations.TryReserve(route.PortId!, source.Identity, route.Locked, DateTimeOffset.UtcNow, out var existing))
            {
                _startupRecovery.TryBegin(route.SourceId);
                var waiting = route with
                {
                    PortId = null,
                    PortName = null,
                    State = RouteState.WaitingForPort,
                    FailureCategory = "DuplicateReservation",
                    FailureMessage = $"Persisted output is already reserved by {existing.Source.Value}.",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _waiting.Enqueue(source.Identity, route.Priority, waiting.FailureMessage!);
                await ReplaceRouteAsync(waiting, route.State, cancellationToken);
                continue;
            }
            _waiting.Remove(source.Identity);
            if (!_startupRecovery.TryBegin(route.SourceId)) continue;
            if (settings.SimulationMode) continue;
            var ownsProcess = _supervisor?.Snapshot().Any(x => x.Source.Value == route.SourceId && x.Running) ?? false;
            if (!ownsProcess && route.State is RouteState.Running or RouteState.Starting or RouteState.Reserved)
            {
                var recovering = route with
                {
                    State = RouteState.Reconnecting,
                    RetryAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    FailureCategory = "RestartRecovery",
                    FailureMessage = "Restoring persisted route after host restart."
                };
                await ReplaceRouteAsync(recovering, route.State, cancellationToken);
                try
                {
                    await RestartReservedRouteAsync(recovering, cancellationToken);
                }
                catch (Exception ex)
                {
                    RuntimeRoute? current;
                    lock (_gate) _routes.TryGetValue(route.SourceId, out current);
                    if (current is null) continue;
                    var detail = LogRedactor.Redact(ex.Message);
                    var failed = RouteStartFailureRecovery.ReleaseAndFail(_reservations, current, source.Identity,
                        string.IsNullOrWhiteSpace(detail) ? "FFmpeg could not be restored after host startup." : detail,
                        DateTimeOffset.UtcNow);
                    await ReplaceRouteAsync(failed, current.State, cancellationToken);
                    await LogAsync("Error", "FFmpeg", "Persisted route recovery could not start FFmpeg; its output reservation was released.",
                        route.SourceId, cancellationToken: cancellationToken);
                }
            }
        }
    }

    private async Task ReplaceRouteAsync(RuntimeRoute route, RouteState? previousState, CancellationToken cancellationToken, bool persistHistory = true)
    {
        RouteState? persistedPrevious;
        RuntimeRoute? currentRoute;
        lock (_gate)
        {
            if (_routes.TryGetValue(route.SourceId, out var current))
            {
                if (previousState is not null && current.State != previousState) return;
                persistedPrevious = current.State;
                currentRoute = current;
            }
            else
            {
                persistedPrevious = previousState;
                currentRoute = null;
            }
            if (persistedPrevious is not null && !_stateMachine.CanTransition(persistedPrevious.Value, route.State))
                throw new InvalidOperationException($"Invalid route transition {persistedPrevious} -> {route.State} for {route.SourceId}.");
            _routes[route.SourceId] = route;
        }
        if (!persistHistory && currentRoute is not null
            && !RouteTelemetryPersistencePolicy.RequiresPersistence(currentRoute, route)) return;
        await store.SaveRouteAsync(route, persistHistory ? persistedPrevious : route.State, cancellationToken);
    }

    private void EnsureSupervisor(string ffmpegPath, bool useWindowsDeckLinkSafeTerminate)
    {
        var inputReadTimeoutMilliseconds = Math.Clamp(
            _settings.Routing.InputReadTimeoutMilliseconds, 500, 30000);
        if (_supervisor is not null
            && string.Equals(_supervisorPath, ffmpegPath, StringComparison.OrdinalIgnoreCase)
            && _supervisorUsesWindowsDeckLinkSafeTerminate == useWindowsDeckLinkSafeTerminate
            && _supervisorInputReadTimeoutMilliseconds == inputReadTimeoutMilliseconds) return;
        if (_supervisor is not null) _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions(ffmpegPath, true,
                TimeSpan.FromMilliseconds(inputReadTimeoutMilliseconds),
                UseWindowsDeckLinkSafeTerminate: useWindowsDeckLinkSafeTerminate),
            TimeSpan.FromSeconds(Math.Clamp(_settings.Routing.GracefulStopSeconds, 1, 30)));
        _supervisor.LifecycleChanged += OnProcessLifecycleChanged;
        _supervisorPath = ffmpegPath;
        _supervisorUsesWindowsDeckLinkSafeTerminate = useWindowsDeckLinkSafeTerminate;
        _supervisorInputReadTimeoutMilliseconds = inputReadTimeoutMilliseconds;
    }

    private void OnProcessLifecycleChanged(RouteProcessLifecycleEvent value) =>
        _ = PersistProcessLifecycleAsync(value);

    private async Task PersistProcessLifecycleAsync(RouteProcessLifecycleEvent value)
    {
        try
        {
            var level = value.State == RouteProcessLifecycleState.ForcedTermination
                || value.State == RouteProcessLifecycleState.Exited && value.ExitCode is not null and not 0
                    ? "Warning"
                    : "Information";
            var exit = value.State == RouteProcessLifecycleState.Exited
                ? $" Exit code: {value.ExitCode?.ToString() ?? "unavailable"}."
                : "";
            var message = $"Owned FFmpeg process {value.ProcessId}: {value.State}.{exit}";
            logger.Log(level == "Warning" ? LogLevel.Warning : LogLevel.Information,
                "{Category}: {Message}", "ProcessLifecycle", message);
            await store.WriteLogAsync(level, "ProcessLifecycle", message, value.Source.Value);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Process lifecycle telemetry could not be persisted: {Message}",
                LogRedactor.Redact(ex.Message));
        }
    }

    private TimeSpan RetryDelay(int count)
    {
        var delays = _settings.Routing.RetryDelaysSeconds.Length == 0 ? new[] { 1, 2, 5, 10, 20, 30 } : _settings.Routing.RetryDelaysSeconds;
        var policy = new RetryPolicy(delays.Select(seconds => TimeSpan.FromSeconds(Math.Max(0, seconds))).ToArray());
        var seconds = policy.GetDelay(count).TotalSeconds;
        var jitter = Random.Shared.NextDouble() * Math.Min(1, seconds * .2);
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private void Publish(string status)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan cpuTime;
        long workingSet;
        using (var process = Process.GetCurrentProcess())
        {
            cpuTime = process.TotalProcessorTime;
            workingSet = process.WorkingSet64;
        }
        double cpu;
        lock (_metricsGate)
        {
            var interval = Math.Max(.001, (now - _lastCpuAt).TotalSeconds);
            cpu = Math.Clamp((cpuTime - _lastCpu).TotalSeconds / (interval * Environment.ProcessorCount) * 100, 0, 100);
            _lastCpu = cpuTime;
            _lastCpuAt = now;
        }

        RouterSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new(_sources.Values.OrderBy(x => x.Identity.Value).ToArray(), _ports.Values.OrderBy(x => x.CardIndex).ThenBy(x => x.SubdeviceIndex).ToArray(),
                _routes.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.SourceName).ToArray(),
                _waiting.Snapshot().Select(x => new QueueItemSnapshot(x.Source.Value, x.Priority, x.Reason, x.Sequence)).ToArray(),
                _servers.Values.OrderBy(x => x.FriendlyName).ToArray(), _validation, _startedAt, now, cpu, workingSet,
                _settings.SimulationMode, _emergencyStopped, status,
                _standbys.Values.OrderBy(value => value.PortId, StringComparer.OrdinalIgnoreCase).ToArray());
            Snapshot = snapshot;
        }
        foreach (var failure in ChangeNotificationDispatcher.Dispatch(Changed))
            logger.LogWarning(failure, "A snapshot subscriber failed; remaining subscribers were still notified.");
        _ = NotifyStatusHubAsync(snapshot.UpdatedAt);
    }

    private async Task NotifyStatusHubAsync(DateTimeOffset updatedAt)
    {
        try { await hub.Clients.All.SendAsync("SnapshotChanged", updatedAt).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "Status hub snapshot notification failed."); }
    }

    private async Task AuditPortConfigurationChangesAsync(OperatorSettings previous, OperatorSettings current,
        IReadOnlyList<DeckLinkPort> knownPorts, string actor, string reason, string backendStatus,
        CancellationToken cancellationToken)
    {
        var previousById = previous.DeckLinkPortOverrides.ToDictionary(value => value.StableId, StringComparer.OrdinalIgnoreCase);
        var currentById = current.DeckLinkPortOverrides.ToDictionary(value => value.StableId, StringComparer.OrdinalIgnoreCase);
        var portsById = knownPorts.ToDictionary(value => value.StableId, StringComparer.OrdinalIgnoreCase);
        foreach (var portId in previousById.Keys.Union(currentById.Keys, StringComparer.OrdinalIgnoreCase))
        {
            previousById.TryGetValue(portId, out var oldValue);
            currentById.TryGetValue(portId, out var newValue);
            var oldState = DescribePortOverride(oldValue);
            var newState = DescribePortOverride(newValue);
            if (oldState.Equals(newState, StringComparison.Ordinal)) continue;
            portsById.TryGetValue(portId, out var port);
            var cardName = port is null ? "" : DeckLinkDisplayName.Card(port);
            var portName = newValue?.FriendlyName ?? oldValue?.FriendlyName ?? (port is null ? "" : DeckLinkDisplayName.Connector(port));
            await store.WriteConfigurationAuditAsync(new(0, DateTimeOffset.UtcNow, "OutputPortConfiguration", portId,
                cardName, portName, oldState, newState, actor, reason, backendStatus), cancellationToken);
            await LogAsync("Information", "ConfigurationAudit",
                $"Output connector {DeckLinkDisplayName.ShortIdentity(portId)} changed by {actor}: {oldState} -> {newState}. {backendStatus}.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task RefreshDeckLinkReferenceStatusAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_settings.SimulationMode || now < _nextDeckLinkReferenceStatusCheck) return;
        _nextDeckLinkReferenceStatusCheck = now + TimeSpan.FromSeconds(2);
        var executable = Path.Combine(AppContext.BaseDirectory, "BroadcastRouter.Server.exe");
        var probe = await DeckLinkIdentityProcessProbe.EnumerateAsync(executable, cancellationToken);
        if (!probe.Success)
        {
            _nextDeckLinkReferenceStatusCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            await LogAsync("Warning", "DeckLinkReference", $"DeckLink reference-status query failed: {probe.Error}",
                cancellationToken: cancellationToken);
            return;
        }
        var hardware = probe.Identities;
        var byHandle = hardware.Where(value => !string.IsNullOrWhiteSpace(value.DeviceHandle))
            .GroupBy(value => value.DeviceHandle, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<(DeckLinkPort Previous, DeckLinkPort Current)>();
        lock (_gate)
        {
            foreach (var pair in _ports.ToArray())
            {
                var port = pair.Value;
                if (port.DeviceHandle is null || !byHandle.TryGetValue(port.DeviceHandle, out var status)) continue;
                var updated = port with
                {
                    HasReferenceInput = status.HasReferenceInput,
                    ReferenceSignalLocked = status.ReferenceSignalLocked
                };
                _ports[pair.Key] = updated;
                if (port.ReferenceSignalLocked != updated.ReferenceSignalLocked
                    || port.HasReferenceInput != updated.HasReferenceInput)
                    changes.Add((port, updated));
            }
        }
        foreach (var change in changes)
        {
            var level = change.Current.HasReferenceInput == true && change.Current.ReferenceSignalLocked == false
                ? "Warning" : "Information";
            var state = change.Current.HasReferenceInput switch
            {
                false => "not supported by this hardware",
                true when change.Current.ReferenceSignalLocked == true => "locked",
                true when change.Current.ReferenceSignalLocked == false => "unlocked; output remains in free-run and routing will keep retrying independently",
                _ => "status unavailable"
            };
            await LogAsync(level, "DeckLinkReference",
                $"Reference status for {DeckLinkDisplayName.Full(change.Current)} is {state}.", cancellationToken: cancellationToken);
        }
    }

    private async Task AuditDeviceRediscoveryAsync(IReadOnlyList<DeckLinkPort> previous,
        IReadOnlyList<DeckLinkPort> current, CancellationToken cancellationToken)
    {
        if (previous.Count == 0) return;
        var previousIds = previous.Select(port => port.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentIds = current.Select(port => port.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = currentIds.Except(previousIds, StringComparer.OrdinalIgnoreCase).Count();
        var removed = previousIds.Except(currentIds, StringComparer.OrdinalIgnoreCase).Count();
        await store.WriteConfigurationAuditAsync(new(0, DateTimeOffset.UtcNow, "DeviceRediscovery", "DECKLINK",
            "DeckLink", "", $"{previous.Count} connector(s)", $"{current.Count} connector(s)", "system",
            "Scheduled or operator-requested hardware enumeration",
            added == 0 && removed == 0 ? "Identity set unchanged" : $"Added {added}; removed {removed}"), cancellationToken);
        if (added > 0 || removed > 0)
            await LogAsync("Warning", "DeckLinkDiscovery",
                $"DeckLink identity set changed during rediscovery: {added} added, {removed} removed. Persisted output-port configuration was not modified.",
                cancellationToken: cancellationToken);
    }

    private static string DescribePortOverride(DeckLinkPortOverride? value) => value is null
        ? "not configured"
        : $"output={value.IsOutputPort}; excluded={value.Reserved}; group={value.PortGroup}; standby={value.StandbyEnabled}; " +
          $"preset={value.StandbyPresetId}; pattern={value.StandbyPattern}; label={value.StandbyLabel}; clock={value.StandbyShowClock}";

    private static string DescribeRouteAssignment(RuntimeRoute? route) => route is null
        ? "not assigned"
        : $"mode={route.AssignmentMode}; desiredPort={route.DesiredPortId ?? "none"}; preset={route.PresetId}; " +
          $"reserveOffline={route.ReserveWhileOffline}; temporaryUse={route.AllowTemporaryUse}; locked={route.Locked}";

    private async Task LogAsync(string level, string category, string message, string? sourceId = null, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        await store.WriteLogAsync(level, category, message, sourceId, correlationId, cancellationToken);
        logger.Log(level switch { "Error" or "Critical" => LogLevel.Error, "Warning" => LogLevel.Warning, _ => LogLevel.Information }, "{Category}: {Message}", category, LogRedactor.Redact(message));
    }

    private static bool Compatible(DeckLinkPort port, OutputPresetProfile preset, string? requiredGroup)
    {
        if (!port.IsAvailable) return false;
        if (!string.IsNullOrWhiteSpace(requiredGroup) && !string.Equals(port.PortGroup, requiredGroup, StringComparison.OrdinalIgnoreCase)) return false;
        if (port.SupportedModes.Count == 0) return true;
        return port.SupportedModes.Any(mode => mode.Width == preset.Width && mode.Height == preset.Height
            && Math.Abs(mode.FramesPerSecond - preset.FrameRateNumerator / (double)Math.Max(1, preset.FrameRateDenominator)) < .02
            && mode.PixelFormat.Equals(preset.PixelFormat, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record WowzaPollResult(
        WowzaServerProfile Profile,
        IReadOnlyList<DiscoveredSource> Discovered,
        ServerHealth Health,
        bool Recovered,
        string? Error);
    private static bool IsPermanent(FfmpegFailureCategory category) => category is FfmpegFailureCategory.Authentication or FfmpegFailureCategory.Codec
        or FfmpegFailureCategory.UnsupportedMedia or FfmpegFailureCategory.DeckLinkFormat or FfmpegFailureCategory.Configuration;
    private static string NewCorrelation() => Guid.NewGuid().ToString("N")[..12];
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private DiscoveredSource[] SourcesCopy() { lock (_gate) return _sources.Values.ToArray(); }
    private RuntimeRoute[] RoutesCopy() { lock (_gate) return _routes.Values.ToArray(); }

    private static OperatorSettings Clone(OperatorSettings value) => JsonSerializer.Deserialize<OperatorSettings>(JsonSerializer.Serialize(value))!;

    private static WowzaServerConfiguration ToConfiguration(WowzaServerProfile profile) => new(profile.FriendlyName, profile.ServerId,
        new Uri(profile.ManagementUrl.EndsWith('/') ? profile.ManagementUrl : profile.ManagementUrl + "/"), $"wowza:{profile.ServerId}", profile.ValidateTlsCertificate,
        profile.RtspHost, profile.RtspPort, Split(profile.Applications), Split(profile.ApplicationInstances), profile.RtspUrlTemplate,
        TimeSpan.FromSeconds(Math.Clamp(profile.PollingIntervalSeconds, 1, 300)), TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 2, 60)),
        profile.Enabled, profile.Priority);

    private static IReadOnlyList<string> Split(string value) => value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static SourceIdentity SourceIdentityFromValue(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 4) throw new FormatException("Persisted source identity is invalid.");
        return new(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3]));
    }

    private static IReadOnlyList<DiscoveredSource> BuildSimulationSources()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            Sim("news-hd.stream", "News HD", 100, 1920, 1080, 25, true, now),
            Sim("studio-b.stream", "Studio B", 80, 1920, 1080, 50, true, now),
            Sim("sports-720.stream", "Sports 720p", 70, 1280, 720, 50, true, now),
            Sim("clean-feed.stream", "Clean Feed", 60, 1920, 1080, 25, false, now),
            Sim("waiting.stream", "Waiting Queue Demo", 20, 1920, 1080, 25, true, now)
        ];
    }

    private static DiscoveredSource Sim(string stream, string name, int priority, int width, int height, double fps, bool audio, DateTimeOffset now)
    {
        var identity = new SourceIdentity("SIM-WOWZA", "live", "_definst_", stream);
        return new(identity, name, new Uri($"rtsp://127.0.0.1:1935/live/{stream}"), SourceState.Ready, priority,
            new MediaProperties("h264", audio ? "aac" : null, width, height, fps, 3_000_000, audio ? 48_000 : null, audio ? 2 : null, true),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "simulation", width == 1920 ? "hd" : "720p" }, LastObservedAt: now);
    }

    private static IReadOnlyList<DeckLinkPort> BuildSimulationPorts()
    {
        var modes = new[]
        {
            new VideoMode(1920, 1080, 25, 1, "uyvy422"), new VideoMode(1920, 1080, 50, 1, "uyvy422"),
            new VideoMode(1280, 720, 50, 1, "uyvy422")
        };
        return
        [
            new("SIM-CARD-A-1", "DeckLink Quad 2 (1)", "DeckLink Quad 2", 0, 0, "PCI:01:00.0", modes, true, "PGM Return 1", "Simulation stable ID", DeviceGroupId: "SIM-CARD-A", IsOutputPort: true),
            new("SIM-CARD-A-2", "DeckLink Quad 2 (2)", "DeckLink Quad 2", 0, 1, "PCI:01:00.0", modes, true, "PGM Return 2", "Simulation stable ID", DeviceGroupId: "SIM-CARD-A", IsOutputPort: true),
            new("SIM-CARD-B-1", "DeckLink Quad 2 (5)", "DeckLink Quad 2", 1, 0, "PCI:02:00.0", modes, true, "Transmission 1", "Simulation stable ID", DeviceGroupId: "SIM-CARD-B", IsOutputPort: true),
            new("SIM-CARD-B-2", "DeckLink Quad 2 (6)", "DeckLink Quad 2", 1, 1, "PCI:02:00.0", modes, true, "Transmission 2", "Simulation stable ID", DeviceGroupId: "SIM-CARD-B", IsOutputPort: true)
        ];
    }

    internal static IReadOnlyList<DeckLinkPort> ApplyOverrides(
        IReadOnlyList<DeckLinkPort> ports,
        IReadOnlyList<DeckLinkCardOverride> cardOverrides,
        IReadOnlyList<DeckLinkPortOverride> portOverrides)
    {
        var cardsById = cardOverrides.ToDictionary(x => x.DeviceGroupId, StringComparer.OrdinalIgnoreCase);
        var portsById = portOverrides.ToDictionary(x => x.StableId, StringComparer.OrdinalIgnoreCase);
        return ports.Select(port =>
        {
            var cardName = port.DeviceGroupId is not null && cardsById.TryGetValue(port.DeviceGroupId, out var card)
                && !string.IsNullOrWhiteSpace(card.FriendlyName) ? card.FriendlyName : port.CardFriendlyName;
            return portsById.TryGetValue(port.StableId, out var value)
                ? port with
                {
                    CardFriendlyName = cardName,
                    FriendlyName = string.IsNullOrWhiteSpace(value.FriendlyName) ? port.FriendlyName : value.FriendlyName,
                    PortGroup = value.PortGroup,
                    Reserved = value.Reserved,
                    IsOutputPort = value.IsOutputPort
                }
                : port with { CardFriendlyName = cardName, IsOutputPort = port.IsOutputPort };
        }).ToArray();
    }

    private sealed record ProbeOutcome(DiscoveredSource Source, StreamProbeResult? Probe,
        SourceMediaMode PreviousMode, SourceMediaMode CurrentMode, bool ModeChanged,
        bool ExtendedVideoConfirmed);
    private sealed record DiscoveryCycleResult(int ObservedSources, int EnabledServers,
        int SuccessfulServers, TimeSpan Elapsed);
}
