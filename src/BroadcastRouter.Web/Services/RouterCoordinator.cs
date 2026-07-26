using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly StartupRouteRecoveryTracker _startupRecovery = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private FfmpegProcessSupervisor? _supervisor;
    private string? _supervisorPath;
    private bool _supervisorUsesWindowsDeckLinkSafeTerminate;
    private OperatorSettings _settings = new();
    private MediaToolValidation _validation = MediaToolValidation.NotConfigured;
    private volatile bool _emergencyStopped;
    private volatile bool _forceDiscovery = true;
    private volatile bool _forceToolValidation = true;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastToolValidation = DateTimeOffset.MinValue;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.UtcNow;

    public event Action? Changed;

    public RouterSnapshot Snapshot { get; private set; } = new([], [], [], [], [], MediaToolValidation.NotConfigured,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, true, false, "Starting");

    public OperatorSettings GetSettings()
    {
        lock (_gate) return Clone(_settings);
    }

    public async Task SaveSettingsAsync(OperatorSettings settings, CancellationToken cancellationToken = default)
    {
        foreach (var rule in settings.Rules)
        {
            RoutingRuleEvaluator.ValidatePattern(rule.ServerPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.ApplicationPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.InstancePattern);
            RoutingRuleEvaluator.ValidatePattern(rule.StreamPattern);
        }
        RuntimeRoute[] dependentRoutes;
        lock (_gate) dependentRoutes = _routes.Values.Where(route => route.State is not RouteState.Released and not RouteState.Disabled).ToArray();
        OutputPresetSelection.EnsureReferencesAvailable(settings.Presets, dependentRoutes.Select(route => route.PresetId));
        await store.SaveSettingsAsync(settings, cancellationToken);
        lock (_gate)
        {
            _settings = Clone(settings);
            _forceDiscovery = true;
            _forceToolValidation = true;
        }
        await LogAsync("Information", "Settings", "Operator settings saved and queued for reconciliation.", cancellationToken: cancellationToken);
        Publish("Settings updated");
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

    internal async Task CommandAsync(string action, string? sourceId = null, string? portId = null, string? presetId = null,
        CancellationToken cancellationToken = default)
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
            _forceDiscovery = true;
            await LogAsync("Information", "Operator", "Hardware and source rescan requested.", cancellationToken: cancellationToken);
            Publish("Rescan requested");
            return;
        }
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source ID is required for this action.", nameof(sourceId));

        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        switch (action)
        {
            case "start":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "restore":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                if (route?.PortId is not null) await RestartReservedRouteAsync(route, cancellationToken);
                else await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "stop":
                await StopRouteAsync(sourceId, forceRelease: false, cancellationToken);
                break;
            case "restart":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                await StopRouteAsync(sourceId, forceRelease: true, cancellationToken);
                await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "reassign":
                RouteControlSafety.EnsureStartAllowed(_emergencyStopped);
                ValidateRequestedPreset(presetId);
                await StopRouteAsync(sourceId, forceRelease: true, cancellationToken);
                await EnsureRouteAsync(sourceId, portId, presetId, manual: true, cancellationToken);
                break;
            case "reprobe":
                lock (_gate)
                {
                    if (_sources.TryGetValue(sourceId, out var source)) _sources[sourceId] = source with { State = SourceState.Probing, Media = null };
                    _forceDiscovery = true;
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
        foreach (var route in await store.LoadRoutesAsync(stoppingToken))
        {
            _routes[route.SourceId] = route;
            if (route.PortId is not null && route.State is not RouteState.Released and not RouteState.Disabled)
                _startupRecovery.Track(route.SourceId);
        }
        await LogAsync("Information", "Host", "BroadcastRouter server started; persisted routes will be reconciled.", cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshToolsAndPortsAsync(stoppingToken);
                await DiscoverAndProbeAsync(stoppingToken);
                await MonitorProcessesAsync(stoppingToken);
                await ReconcileRoutesAsync(stoppingToken);
                Publish("Running");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Router reconciliation cycle failed");
                await LogAsync("Error", "Coordinator", $"Reconciliation cycle failed: {ex.Message}", cancellationToken: stoppingToken);
                Publish("Reconciliation degraded");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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
        if (settings.SimulationMode)
        {
            _validation = new(ToolValidationState.Valid, "simulation-ffmpeg 1.0", "simulation-ffprobe 1.0", true, true, 4,
                ["Simulation mode: no real FFmpeg or DeckLink device is opened."], DateTimeOffset.UtcNow);
            var simulatedPorts = ApplyOverrides(BuildSimulationPorts(), settings.DeckLinkPortOverrides);
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
        IReadOnlyList<DeckLinkPort> ports = [];
        if (_validation.DeckLinkCompiled && File.Exists(settings.MediaTools.FfmpegPath))
            ports = ApplyOverrides(await new FfmpegDeckLinkEnumerator(settings.MediaTools.FfmpegPath).EnumerateAsync(cancellationToken), settings.DeckLinkPortOverrides);
        lock (_gate)
        {
            _ports.Clear();
            foreach (var port in ports) _ports[port.StableId] = port;
        }
        foreach (var port in ports) await store.UpsertPortAsync(port, cancellationToken);
        await LogAsync(_validation.CanStartHardwareRoutes ? "Information" : "Error", "MediaTools",
            _validation.CanStartHardwareRoutes ? $"Validation passed; {ports.Count} DeckLink output(s) available." : "Validation failed; hardware routes are blocked.", cancellationToken: cancellationToken);
    }

    private async Task DiscoverAndProbeAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var minimumPoll = settings.WowzaServers.Where(x => x.Enabled).Select(x => Math.Clamp(x.PollingIntervalSeconds, 1, 300)).DefaultIfEmpty(2).Min();
        if (!_forceDiscovery && DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromSeconds(minimumPoll)) return;
        _forceDiscovery = false;
        _lastDiscovery = DateTimeOffset.UtcNow;

        var observations = new List<DiscoveredSource>();
        var successfullyPolledServerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.SimulationMode) observations.AddRange(BuildSimulationSources());
        else
        {
            foreach (var profile in settings.WowzaServers.Where(x => x.Enabled))
            {
                try
                {
                    var password = string.IsNullOrWhiteSpace(profile.ProtectedPassword) ? "" : WindowsDpapi.Unprotect(profile.ProtectedPassword);
                    var server = ToConfiguration(profile);
                    using var client = httpClientFactory.CreateClient(profile.ValidateTlsCertificate ? "WowzaValidated" : "WowzaInsecure");
                    var provider = new WowzaDiscoveryProvider(client, server, new StaticCredentialResolver(new CredentialValue(profile.Username, password)));
                    var discovered = await provider.DiscoverAsync(cancellationToken);
                    observations.AddRange(discovered);
                    successfullyPolledServerIds.Add(profile.ServerId);
                    lock (_gate) _servers[profile.ServerId] = new(profile.ServerId, profile.FriendlyName, true, true, discovered.Count, "Discovery succeeded.", DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    lock (_gate) _servers[profile.ServerId] = new(profile.ServerId, profile.FriendlyName, false, false, 0, LogRedactor.Redact(ex.Message), DateTimeOffset.UtcNow);
                    await LogAsync("Warning", "WowzaDiscovery", $"{profile.ServerId} discovery failed: {ex.Message}. Healthy routes were retained.", cancellationToken: cancellationToken);
                }
            }
        }

        foreach (var manual in settings.ManualSources.Where(x => x.Enabled))
        {
            if (!Uri.TryCreate(manual.RtspUrl, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp") continue;
            var identity = new SourceIdentity("MANUAL", "manual", "_definst_", manual.StableId);
            observations.Add(new(identity, manual.FriendlyName, uri, SourceState.PublisherActive, manual.Priority, Tags: new HashSet<string> { "manual" },
                FixedPortId: EmptyToNull(manual.FixedPortId), AssignmentLocked: manual.Locked, LastObservedAt: DateTimeOffset.UtcNow));
        }

        var enabledServerIds = settings.WowzaServers.Where(profile => profile.Enabled).Select(profile => profile.ServerId).ToArray();
        var staleSources = SourceObservationReconciler.FindStaleSources(
            SourcesCopy(), observations, enabledServerIds, successfullyPolledServerIds, settings.SimulationMode);
        if (staleSources.Count > 0)
        {
            lock (_gate)
            {
                foreach (var stale in staleSources)
                {
                    _sources.Remove(stale.Identity.Value);
                    _waiting.Remove(stale.Identity);
                    _sourceReadySince.Remove(stale.Identity.Value);
                    if (_routes.TryGetValue(stale.Identity.Value, out var route) && !route.Locked)
                        _sourceMissingSince.TryAdd(stale.Identity.Value, DateTimeOffset.UtcNow);
                }
            }
            foreach (var stale in staleSources) await store.DeleteSourceAsync(stale.Identity.Value, cancellationToken);
            await LogAsync("Information", "Discovery", $"Removed {staleSources.Count} stale source observation(s).", cancellationToken: cancellationToken);
        }

        lock (_gate)
        {
            var activeHealthIds = enabledServerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (settings.SimulationMode) activeHealthIds.Add("SIM-WOWZA");
            foreach (var staleServerId in _servers.Keys.Where(id => !activeHealthIds.Contains(id)).ToArray())
                _servers.Remove(staleServerId);
        }

        foreach (var source in observations)
        {
            lock (_gate) _sourceMissingSince.Remove(source.Identity.Value);
            var probed = source;
            if (source.Media is null)
            {
                var probe = settings.SimulationMode
                    ? await new SimulationStreamProbe().ProbeAsync(source.RtspUri, cancellationToken)
                    : await new FfprobeStreamProbe(settings.MediaTools.FfprobePath, TimeSpan.FromSeconds(8)).ProbeAsync(source.RtspUri, cancellationToken);
                probed = source with
                {
                    State = probe.FramesReceived ? SourceState.Ready : probe.Opened ? SourceState.UnsupportedMedia : SourceState.RtspUnavailable,
                    Media = probe.Media
                };
                if (!probe.FramesReceived)
                    await LogAsync("Warning", "Probe", $"Probe failed: {probe.FailureCategory}: {probe.Detail}", source.Identity.Value, cancellationToken: cancellationToken);
            }
            lock (_gate)
            {
                _sources[probed.Identity.Value] = probed;
                if (probed.State == SourceState.Ready) _sourceReadySince.TryAdd(probed.Identity.Value, DateTimeOffset.UtcNow);
                else _sourceReadySince.Remove(probed.Identity.Value);
            }
            await store.UpsertSourceAsync(probed, cancellationToken);
        }

        await ReleaseExpiredMissingRoutesAsync(settings, cancellationToken);

        if (settings.SimulationMode)
            lock (_gate) _servers["SIM-WOWZA"] = new("SIM-WOWZA", "Simulated Wowza", true, true, observations.Count, "Simulation API healthy.", DateTimeOffset.UtcNow);
    }

    private async Task ReconcileRoutesAsync(CancellationToken cancellationToken)
    {
        if (_emergencyStopped) return;
        var settings = GetSettings();
        await RestoreReservationsAndProcessesAsync(settings, cancellationToken);
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
            if (route is null || route.State is RouteState.Released or RouteState.Known or RouteState.Ready or RouteState.WaitingForPort)
                await EnsureRouteAsync(source.Identity.Value, null, null, manual: false, cancellationToken);
            else if (route.State is RouteState.Reconnecting or RouteState.Fallback
                     && route.RetryAt <= DateTimeOffset.UtcNow
                     && SourceHasBeenReadyLongEnough(route.SourceId, settings.Routing.StableRestoreSeconds))
                await RestartReservedRouteAsync(route, cancellationToken);
        }
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
            ports = _ports.Values.ToArray();
            settings = Clone(_settings);
            _routes.TryGetValue(sourceId, out previousRoute);
        }
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
        var route = new RuntimeRoute(sourceId, source.FriendlyName, port.StableId, port.FriendlyName ?? port.FfmpegName, presetProfile.Id,
            RouteState.Reserved, manual ? AssignmentMode.Manual : assignment.Mode, decision.Locked, decision.Priority, 0,
            null, null, null, 0, 0, null, now, null, null);
        await ReplaceRouteAsync(route, previousRoute?.State, cancellationToken);
        try
        {
            await StartRouteAsync(route, source, port, presetProfile, cancellationToken);
        }
        catch (Exception ex)
        {
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
        var starting = route with { State = RouteState.Starting, UpdatedAt = DateTimeOffset.UtcNow, FailureCategory = null, FailureMessage = null, RetryAt = null };
        await ReplaceRouteAsync(starting, route.State, cancellationToken);
        if (_settings.SimulationMode)
        {
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

    private async Task MonitorProcessesAsync(CancellationToken cancellationToken)
    {
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
            else if (process.Running && FfmpegStallDetector.IsStalled(true, progress, now, TimeSpan.FromSeconds(_settings.Routing.StallTimeoutSeconds)))
            {
                await _supervisor.StopAsync(process.Source, cancellationToken);
                await ScheduleRetryAsync(route, "VideoStalled", "FFmpeg remained alive but stopped producing progress.", cancellationToken);
            }
            else if (process.Running)
            {
                var state = route.State == RouteState.Fallback ? RouteState.Fallback : progress?.Frame > 0 ? RouteState.Running : RouteState.Starting;
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
                    UpdatedAt = DateTimeOffset.UtcNow
                }, route.State, cancellationToken, persistHistory: state != route.State);
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
    }

    private async Task ScheduleRetryAsync(RuntimeRoute route, string category, string detail, CancellationToken cancellationToken)
    {
        var count = route.RestartCount + 1;
        if (RetryLimitPolicy.IsExhausted(count, _settings.Routing.MaxRetryAttempts))
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
        var retry = route with { State = RouteState.Reconnecting, RestartCount = count, FailureCategory = category,
            FailureMessage = string.IsNullOrWhiteSpace(detail) ? "FFmpeg stopped unexpectedly." : detail,
            RetryAt = DateTimeOffset.UtcNow + RetryDelay(count), UpdatedAt = DateTimeOffset.UtcNow };
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

    private async Task RestartReservedRouteAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        if (route.PortId is null) return;
        DiscoveredSource? source;
        DeckLinkPort? port;
        OutputPresetProfile? preset;
        lock (_gate)
        {
            _sources.TryGetValue(route.SourceId, out source);
            _ports.TryGetValue(route.PortId, out port);
            preset = _settings.Presets.FirstOrDefault(x => x.Id == route.PresetId);
        }
        if (source is null || port is null || preset is null) return;
        if (_simulationFaults.ContainsKey(route.SourceId)) return;
        if (_supervisor is not null && route.State == RouteState.Fallback) await _supervisor.StopAsync(source.Identity, cancellationToken);
        await StartRouteAsync(route, source, port, preset, cancellationToken);
    }

    private async Task StartFallbackAsync(RuntimeRoute route, CancellationToken cancellationToken)
    {
        if (route.PortId is null) return;
        DeckLinkPort? port;
        OutputPresetProfile? preset;
        lock (_gate)
        {
            _ports.TryGetValue(route.PortId, out port);
            preset = _settings.Presets.FirstOrDefault(x => x.Id == route.PresetId);
        }
        if (port is null || preset is null) throw new InvalidOperationException("The reserved port or output preset is unavailable.");
        EnsureSupervisor(_settings.MediaTools.FfmpegPath, _validation.WindowsDeckLinkSafeTerminateSupported);
        await _supervisor!.StartFallbackAsync(SourceIdentityFromValue(route.SourceId), port, preset.ToDomain(), preset.StandbyMode, preset.StandbyValue, cancellationToken);
    }

    private async Task StopRouteAsync(string sourceId, bool forceRelease, CancellationToken cancellationToken)
    {
        var routeGate = _routeGates.GetOrAdd(sourceId, static _ => new SemaphoreSlim(1, 1));
        await routeGate.WaitAsync(cancellationToken);
        try
        {
        RuntimeRoute? route;
        lock (_gate) _routes.TryGetValue(sourceId, out route);
        if (route is null) return;
        RouteControlSafety.EnsureStopAllowed(route.Locked, forceRelease);
        var identity = SourceIdentityFromValue(sourceId);
        _waiting.Remove(identity);
        lock (_gate)
        {
            _sourceMissingSince.Remove(sourceId);
            _sourceReadySince.Remove(sourceId);
        }
        if (_supervisor is not null) await _supervisor.StopAsync(identity, cancellationToken);
        if (route.PortId is not null)
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
        await ReplaceRouteAsync(route with { State = RouteState.Released, PortId = null, PortName = null, UpdatedAt = DateTimeOffset.UtcNow }, route.State, cancellationToken);
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
        var candidates = RoutesCopy().Where(x => x.PortId is not null && x.State is not RouteState.Released and not RouteState.Disabled)
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
                var waiting = route with { PortId = null, PortName = null, State = RouteState.WaitingForPort,
                    FailureCategory = "DuplicateReservation", FailureMessage = $"Persisted output is already reserved by {existing.Source.Value}.", UpdatedAt = DateTimeOffset.UtcNow };
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
                var recovering = route with { State = RouteState.Reconnecting, RetryAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    FailureCategory = "RestartRecovery", FailureMessage = "Restoring persisted route after host restart." };
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
        lock (_gate)
        {
            if (_routes.TryGetValue(route.SourceId, out var current))
            {
                if (previousState is not null && current.State != previousState) return;
                persistedPrevious = current.State;
            }
            else persistedPrevious = previousState;
            if (persistedPrevious is not null && !_stateMachine.CanTransition(persistedPrevious.Value, route.State))
                throw new InvalidOperationException($"Invalid route transition {persistedPrevious} -> {route.State} for {route.SourceId}.");
            _routes[route.SourceId] = route;
        }
        await store.SaveRouteAsync(route, persistHistory ? persistedPrevious : route.State, cancellationToken);
    }

    private void EnsureSupervisor(string ffmpegPath, bool useWindowsDeckLinkSafeTerminate)
    {
        if (_supervisor is not null
            && string.Equals(_supervisorPath, ffmpegPath, StringComparison.OrdinalIgnoreCase)
            && _supervisorUsesWindowsDeckLinkSafeTerminate == useWindowsDeckLinkSafeTerminate) return;
        if (_supervisor is not null) _supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions(ffmpegPath, true, TimeSpan.FromSeconds(10),
                UseWindowsDeckLinkSafeTerminate: useWindowsDeckLinkSafeTerminate),
            TimeSpan.FromSeconds(Math.Clamp(_settings.Routing.GracefulStopSeconds, 1, 30)));
        _supervisorPath = ffmpegPath;
        _supervisorUsesWindowsDeckLinkSafeTerminate = useWindowsDeckLinkSafeTerminate;
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
        RouterSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            using var process = Process.GetCurrentProcess();
            var cpuTime = process.TotalProcessorTime;
            var interval = Math.Max(.001, (now - _lastCpuAt).TotalSeconds);
            var cpu = Math.Clamp((cpuTime - _lastCpu).TotalSeconds / (interval * Environment.ProcessorCount) * 100, 0, 100);
            _lastCpu = cpuTime;
            _lastCpuAt = now;
            snapshot = new(_sources.Values.OrderBy(x => x.Identity.Value).ToArray(), _ports.Values.OrderBy(x => x.CardIndex).ThenBy(x => x.SubdeviceIndex).ToArray(),
                _routes.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.SourceName).ToArray(),
                _waiting.Snapshot().Select(x => new QueueItemSnapshot(x.Source.Value, x.Priority, x.Reason, x.Sequence)).ToArray(),
                _servers.Values.OrderBy(x => x.FriendlyName).ToArray(), _validation, _startedAt, now, cpu, process.WorkingSet64,
                _settings.SimulationMode, _emergencyStopped, status);
            Snapshot = snapshot;
        }
        try { Changed?.Invoke(); } catch { }
        _ = hub.Clients.All.SendAsync("SnapshotChanged", snapshot.UpdatedAt);
    }

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
            new("SIM-CARD-A-1", "DeckLink Quad 2 (1)", "DeckLink Quad 2", 0, 0, "PCI:01:00.0", modes, true, "PGM Return 1", "Simulation stable ID"),
            new("SIM-CARD-A-2", "DeckLink Quad 2 (2)", "DeckLink Quad 2", 0, 1, "PCI:01:00.0", modes, true, "PGM Return 2", "Simulation stable ID"),
            new("SIM-CARD-B-1", "DeckLink Quad 2 (5)", "DeckLink Quad 2", 1, 0, "PCI:02:00.0", modes, true, "Transmission 1", "Simulation stable ID"),
            new("SIM-CARD-B-2", "DeckLink Quad 2 (6)", "DeckLink Quad 2", 1, 1, "PCI:02:00.0", modes, true, "Transmission 2", "Simulation stable ID")
        ];
    }

    private static IReadOnlyList<DeckLinkPort> ApplyOverrides(IReadOnlyList<DeckLinkPort> ports, IReadOnlyList<DeckLinkPortOverride> overrides)
    {
        var byId = overrides.ToDictionary(x => x.StableId, StringComparer.OrdinalIgnoreCase);
        return ports.Select(port => byId.TryGetValue(port.StableId, out var value)
            ? port with { FriendlyName = string.IsNullOrWhiteSpace(value.FriendlyName) ? port.FriendlyName : value.FriendlyName, PortGroup = value.PortGroup, Reserved = value.Reserved }
            : port).ToArray();
    }
}
