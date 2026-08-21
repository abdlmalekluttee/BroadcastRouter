using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;

if (args is ["--write-simulation-settings", var databasePath, var portText, var authenticationText]
    && int.TryParse(portText, out var runtimePort) && bool.TryParse(authenticationText, out var runtimeAuthentication))
{
    var runtimeStore = new SqliteDataStore(databasePath);
    await runtimeStore.InitializeAsync();
    var runtimeSettings = await runtimeStore.LoadSettingsAsync();
    runtimeSettings.SimulationMode = true;
    runtimeSettings.Security.BindAddress = "127.0.0.1";
    runtimeSettings.Security.Port = runtimePort;
    runtimeSettings.Security.RequireAuthentication = runtimeAuthentication;
    runtimeSettings.Security.AllowedNetworks = "127.0.0.1/32;::1/128";
    await runtimeStore.SaveSettingsAsync(runtimeSettings);
    return 0;
}

var tests = new (string Name, Action Body)[]
{
    ("Persistent source identity", SourceIdentityIsUnambiguous),
    ("Unique generated IDs skip collisions", GeneratedIdsSkipCollisions),
    ("RTSP URL generation", RtspUrlIsGeneratedAndEscaped),
    ("Invalid RTSP token rejected", InvalidRtspTokenIsRejected),
    ("Atomic duplicate reservation prevention", DuplicateReservationIsPrevented),
    ("Concurrent reservation stress", ConcurrentReservationStressAllowsOneOwner),
    ("Locked reservation protection", LockedReservationRequiresForce),
    ("Reservation release distinguishes free and foreign ownership", ReservationReleaseDistinguishesMissingAndForeignOwnership),
    ("Startup failure releases reservation", StartupFailureReleasesReservation),
    ("Missing-source lease retention", MissingSourceLeaseRetentionHonorsLockAndGrace),
    ("Emergency stop blocks route starts", EmergencyStopBlocksRouteStarts),
    ("Locked route stop is refused before release", LockedRouteStopIsRefused),
    ("Administrator route commands are authorized", RouteCommandsRequireAdministrator),
    ("Priority waiting queue", HigherPriorityDequeuesFirst),
    ("Route transition validation", InvalidStateJumpIsRejected),
    ("Fallback and reconnect recovery can reacquire a reservation", RecoveryCanReacquireReservation),
    ("Missing live owner enters retry before reservation", MissingLiveOwnerEntersRetryBeforeReservation),
    ("Saved routes retry without waiting for discovery", SavedRoutesRetryWithoutDiscovery),
    ("Owned video progress outranks a stale probe", OwnedVideoProgressOutranksStaleProbe),
    ("Saved route enters retry on transient source loss", SavedRouteEntersRetryOnTransientLoss),
    ("Saved recovery attempt receives startup grace", SavedRecoveryAttemptReceivesStartupGrace),
    ("Fast publisher disconnect requires confirmation", FastPublisherDisconnectRequiresConfirmation),
    ("Publisher return preserves validated readiness", PublisherReturnPreservesValidatedReadiness),
    ("Connected publisher accelerates saved waiting route", ConnectedPublisherAcceleratesSavedWaitingRoute),
    ("Automatic assignment", AutomaticAssignmentUsesOnePort),
    ("Automatic assignment ignores input-only ports", AutomaticAssignmentIgnoresInputOnlyPorts),
    ("Saved routing priority and protection", SavedRoutingPriorityAndProtection),
    ("Simulation discovery", SimulationReturnsUsableVideo),
    ("Retry policy caps delay", RetryPolicyCapsAtMaximum),
    ("Retry attempt cap is opt-in", RetryAttemptCapIsOptIn),
    ("Repeated coordinator failures are log-throttled", RepeatedFailuresAreLogThrottled),
    ("Faulted snapshot subscriber does not block others", FaultedSnapshotSubscriberDoesNotBlockOthers),
    ("Route telemetry updates skip persistence", RouteTelemetryUpdatesSkipPersistence),
    ("Durable route changes require persistence", DurableRouteChangesRequirePersistence),
    ("Coordinator liveness detects a blocked cycle", CoordinatorLivenessDetectsBlockedCycle),
    ("Coordinator watchdog is registered and health-aware", CoordinatorWatchdogIsRegisteredAndHealthAware),
    ("Startup route recovery is single shot", StartupRouteRecoveryIsSingleShot),
    ("FFmpeg command uses argument list", FfmpegCommandUsesArgumentList),
    ("FFmpeg audio-led route generates continuous black video", FfmpegAudioLedRouteGeneratesBlackVideo),
    ("FFmpeg video-only route generates explicit silence", FfmpegVideoOnlyRouteGeneratesSilence),
    ("FFmpeg interlaced output is explicit", FfmpegInterlacedOutputIsExplicit),
    ("FFmpeg display redacts credentials", FfmpegDisplayRedactsCredentials),
    ("Browser preview is tokenized with VU overlay", BrowserPreviewIsTokenizedWithVuOverlay),
    ("FFmpeg progress parsing", FfmpegProgressIsParsed),
    ("FFmpeg stall detection", FfmpegStallIsDetected),
    ("FFmpeg duplicate-frame input freeze detection", FfmpegDuplicateFrameFreezeIsDetected),
    ("FFmpeg rapid duplicate burst detection", FfmpegRapidDuplicateBurstIsDetected),
    ("FFmpeg first-progress timeout", FfmpegFirstProgressTimeoutIsDetected),
    ("Windows job kills orphaned media process", WindowsJobKillsOrphanedProcess),
    ("Owned process stop and restart are serialized", OwnedProcessStopAndRestartAreSerialized),
    ("Fallback ownership is reaped before live recovery", FallbackOwnershipIsReapedBeforeLiveRecovery),
    ("Uncooperative owned process cleanup is bounded", UncooperativeOwnedProcessCleanupIsBounded),
    ("FFmpeg failure classification", FfmpegFailureIsClassified),
    ("DeckLink output failures are not mislabeled as network", DeckLinkOutputFailureIsClassified),
    ("FFmpeg RTSP protocol loss is retryable input failure", FfmpegRtspProtocolLossIsRetryable),
    ("Owned supervisor captures RTSP protocol loss", SupervisorCapturesRtspProtocolLoss),
    ("Post-startup DeckLink media starvation is fatal", PostStartupDeckLinkMediaStarvationIsDetected),
    ("DeckLink startup starvation is ignored", DeckLinkStartupStarvationIsIgnored),
    ("Owned supervisor captures DeckLink media starvation", SupervisorCapturesDeckLinkMediaStarvation),
    ("FFprobe media parsing", FfprobeMediaIsParsed),
    ("FFprobe scan type parsing", FfprobeScanTypeIsParsed),
    ("FFprobe accepts audio-led sparse video", FfprobeAcceptsAudioLedSparseVideo),
    ("FFprobe accepts delayed but continuous video", FfprobeAcceptsDelayedContinuousVideo),
    ("Adaptive probe promotes long-GOP video", AdaptiveProbePromotesLongGopVideo),
    ("FFprobe ignores isolated video frames over live audio", FfprobeIgnoresIsolatedVideoFrames),
    ("FFprobe accepts audio-only input with packets", FfprobeAcceptsAudioOnlyInput),
    ("FFprobe rejects metadata-only video", FfprobeRequiresReadFrames),
    ("Probe readiness accepts audio and remains fail-closed", ProbeReadinessAcceptsAudioAndRejectsMetadataOnly),
    ("Probe readiness retains audio-led mode", ProbeReadinessRetainsAudioLedMode),
    ("Audio-led mode restores after stable video confirmation", ProbeReadinessRestoresStableVideo),
    ("Probe media mode resists alternating sparse-video samples", ProbeMediaModeResistsFlapping),
    ("FFprobe rejects malformed output", FfprobeRejectsMalformedOutput),
    ("Wowza incoming stream parsing", WowzaIncomingStreamsAreParsed),
    ("Wowza discovery retains disconnected streams", WowzaDiscoveryRetainsDisconnectedStreams),
    ("Renamed Wowza source is pruned", RenamedWowzaSourceIsPruned),
    ("Failed Wowza poll retains source", FailedWowzaPollRetainsSource),
    ("Log credential redaction", AuthenticatedUrisAreRedacted),
    ("Diagnostics omit sensitive data", DiagnosticsOmitSensitiveData),
    ("Windows DPAPI credential round trip", DpapiCredentialRoundTrips),
    ("Atomic operator settings persistence", OperatorSettingsPersistAtomically),
    ("DeckLink sink enumeration parsing", DeckLinkSinksAreParsed),
    ("DeckLink persistent hardware identity", DeckLinkPersistentIdentityIsResolved),
    ("DeckLink identity polling is isolated and bounded", DeckLinkIdentityPollingIsIsolatedAndBounded),
    ("DeckLink human identity labels", DeckLinkHumanIdentityLabelsAreStable),
    ("DeckLink visual asset catalog matching", DeckLinkVisualAssetCatalogMatchesSafely),
    ("DeckLink official software update parsing", DeckLinkOfficialSoftwareUpdateIsParsed),
    ("DeckLink identity reference migration", DeckLinkIdentityReferencesAreMigrated),
    ("DeckLink migration deferral requires legacy references", DeckLinkMigrationDefersOnlyLegacyReferences),
    ("Wowza instance discovery endpoint", WowzaInstanceEndpointIsCorrect),
    ("Common broadcast presets", CommonPresetsAreAvailable),
    ("Output scan selection round trip", OutputScanSelectionRoundTrips),
    ("Manual route preset selection", ManualRoutePresetSelectionIsValidated),
    ("Source route action reflects current route", SourceRouteActionReflectsCurrentRoute),
    ("Source refresh is backend-confirmed and source-only", SourceRefreshIsBackendConfirmed),
    ("Routing rule wildcard evaluation", RoutingRuleWildcardMatches),
    ("Routing regex validation", InvalidRoutingRegexIsRejected),
    ("Fallback command is uncompressed", FallbackCommandIsSafeAndUncompressed),
    ("Per-port standby command is broadcast safe", PortStandbyCommandIsBroadcastSafe),
    ("Windows service installer is automatic and recoverable", WindowsServiceInstallerIsAutomaticAndRecoverable),
    ("SQLite settings persistence", SqliteSettingsPersist),
    ("Stale settings revisions are rejected", StaleSettingsRevisionIsRejected),
    ("All GUI settings round trip", AllGuiSettingsRoundTrip),
    ("Settings reject invalid GUI values", SettingsRejectInvalidGuiValues),
    ("Settings reject ambiguous DeckLink card names", SettingsRejectAmbiguousDeckLinkCardNames),
    ("Settings reject invalid CIDR ranges", SettingsRejectInvalidCidrRanges),
    ("Network exposure and proxy trust are fail-closed", NetworkExposureIsFailClosed),
    ("Settings reject embedded credentials", SettingsRejectEmbeddedCredentials),
    ("Failed validation preserves active settings", FailedValidationPreservesActiveSettings),
    ("SQLite route restart recovery", SqliteRoutesRestore),
    ("SQLite source inventory survives offline", SqliteSourcesRestore),
    ("SQLite structured log redaction", SqliteLogsAreRedacted),
    ("SQLite configuration audit persistence", SqliteConfigurationAuditPersists),
    ("SQLite integrity check", SqliteIntegrityIsOk),
    ("Default configuration is production safe", DefaultConfigurationIsProductionSafe)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures.Add($"{test.Name}: {ex.Message}"); Console.WriteLine($"FAIL  {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void SavedRoutesRetryWithoutDiscovery()
{
    var now = DateTimeOffset.UtcNow;
    var source = new DiscoveredSource(
        new SourceIdentity("WOWZA", "live", "_definst_", "rapid.stream"),
        "Rapid stream", new Uri("rtsp://127.0.0.1/live/rapid.stream"),
        SourceState.PublisherDisconnected, 0,
        new MediaProperties("h264", "aac", 1920, 1080, 25, 3_000_000, 48_000, 2, true));
    var saved = new RuntimeRoute(
        SourceId: source.Identity.Value,
        SourceName: source.FriendlyName,
        PortId: "PORT-1",
        PortName: "Output 1",
        PresetId: "1080p25",
        State: RouteState.Fallback,
        AssignmentMode: AssignmentMode.Manual,
        Locked: true,
        Priority: 0,
        RestartCount: 1,
        Frame: null,
        Fps: null,
        Speed: null,
        DroppedFrames: 0,
        DuplicatedFrames: 0,
        StartedAt: now,
        UpdatedAt: now,
        FailureCategory: "InputSessionLost",
        FailureMessage: "Rapid interruption",
        RetryAt: now,
        DesiredPortId: "PORT-1",
        DesiredPortName: "Output 1");
    True(RapidStreamRecoveryPolicy.CanAttemptReservedRecovery(saved, source));
    True(!RapidStreamRecoveryPolicy.CanAttemptReservedRecovery(
        saved with { AssignmentMode = AssignmentMode.Automatic }, source));
    True(!RapidStreamRecoveryPolicy.CanAttemptReservedRecovery(
        saved, source with { State = SourceState.Disabled }));
}

static void OwnedVideoProgressOutranksStaleProbe()
{
    var video = new DiscoveredSource(
        new SourceIdentity("WOWZA", "live", "_definst_", "rapid.stream"),
        "Rapid stream", new Uri("rtsp://127.0.0.1/live/rapid.stream"),
        SourceState.RtspUnavailable, 0,
        new MediaProperties("h264", "aac", 1920, 1080, 25, 3_000_000, 48_000, 2, true));
    True(RapidStreamRecoveryPolicy.IsEffectivelyActive(video, ownedLiveVideoIsAdvancing: true));
    True(!RapidStreamRecoveryPolicy.IsEffectivelyActive(video, ownedLiveVideoIsAdvancing: false));
    True(!RapidStreamRecoveryPolicy.IsEffectivelyActive(
        video with { Media = video.Media! with { HasUsableVideo = false } },
        ownedLiveVideoIsAdvancing: true));
}

static void SavedRouteEntersRetryOnTransientLoss()
{
    var now = DateTimeOffset.UtcNow;
    var route = RapidSavedRoute(RouteState.Running, now);
    True(RapidStreamRecoveryPolicy.ShouldEnterSavedRetry(route, sourceIsEffectivelyActive: false));
    True(!RapidStreamRecoveryPolicy.ShouldEnterSavedRetry(route, sourceIsEffectivelyActive: true));
    True(!RapidStreamRecoveryPolicy.ShouldEnterSavedRetry(
        route with { AssignmentMode = AssignmentMode.Automatic }, sourceIsEffectivelyActive: false));
}

static void SavedRecoveryAttemptReceivesStartupGrace()
{
    var now = DateTimeOffset.UtcNow;
    var route = RapidSavedRoute(RouteState.Starting, now);
    True(RapidStreamRecoveryPolicy.ShouldKeepStartingAttempt(
        route, false, true, now.AddMilliseconds(-500), now, TimeSpan.FromSeconds(3)));
    True(!RapidStreamRecoveryPolicy.ShouldKeepStartingAttempt(
        route, false, true, now.AddSeconds(-4), now, TimeSpan.FromSeconds(3)));
    True(!RapidStreamRecoveryPolicy.ShouldKeepStartingAttempt(
        route, false, false, now.AddMilliseconds(-500), now, TimeSpan.FromSeconds(3)));
}

static void FastPublisherDisconnectRequiresConfirmation()
{
    var detector = new PublisherDisconnectDetector(requiredObservations: 2);
    True(!detector.Observe("WOWZA/live/_definst_/rapid.stream", publisherConnected: false));
    True(detector.Observe("WOWZA/live/_definst_/rapid.stream", publisherConnected: false));
    True(detector.Observe("WOWZA/live/_definst_/rapid.stream", publisherConnected: false));
    True(detector.ObserveConnected("WOWZA/live/_definst_/rapid.stream"));
    True(!detector.ObserveConnected("WOWZA/live/_definst_/rapid.stream"));
    True(!detector.Observe("WOWZA/live/_definst_/rapid.stream", publisherConnected: false));
    True(!detector.ObserveConnected("WOWZA/live/_definst_/rapid.stream"));
    detector.Forget("WOWZA/live/_definst_/rapid.stream");
    True(!detector.Observe("WOWZA/live/_definst_/rapid.stream", publisherConnected: false));
}

static void PublisherReturnPreservesValidatedReadiness()
{
    var now = DateTimeOffset.UtcNow;
    var identity = new SourceIdentity("WOWZA", "live", "_definst_", "rapid.stream");
    var known = new DiscoveredSource(identity, "Rapid stream", new Uri("rtsp://127.0.0.1/live/rapid.stream"),
        SourceState.PublisherDisconnected, 0,
        new MediaProperties("h264", "aac", 1920, 1080, 25, 3_000_000, 48_000, 2, true));
    var unknown = known with { Media = null };

    var restoredKnown = RapidStreamRecoveryPolicy.MarkPublisherRestored(known, now);
    var restoredUnknown = RapidStreamRecoveryPolicy.MarkPublisherRestored(unknown, now);
    Equal(SourceState.Ready, restoredKnown.State);
    Equal(SourceState.PublisherActive, restoredUnknown.State);
    Equal(now, restoredKnown.LastObservedAt);
}

static void ConnectedPublisherAcceleratesSavedWaitingRoute()
{
    var now = DateTimeOffset.UtcNow;
    True(RapidStreamRecoveryPolicy.ShouldSupervisePublisher(RapidSavedRoute(RouteState.Running, now)));
    True(RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.Reconnecting, now)));
    True(RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.Fallback, now)));
    True(RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.WaitingForStream, now)));
    True(RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.Starting, now), ownsLiveProcess: false));
    True(RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.Running, now), ownsLiveProcess: false));
    True(!RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.Starting, now), ownsLiveProcess: true));
    True(!RapidStreamRecoveryPolicy.CanAccelerateConnectedPublisherRecovery(
        RapidSavedRoute(RouteState.WaitingForStream, now) with { AssignmentMode = AssignmentMode.Automatic }));
    True(!RapidStreamRecoveryPolicy.ShouldSupervisePublisher(
        RapidSavedRoute(RouteState.Released, now)));

    var staleStarting = RapidStreamRecoveryPolicy.MarkConnectedPublisherRecoveryDue(
        RapidSavedRoute(RouteState.Starting, now), now.AddSeconds(1), ownsLiveProcess: false);
    Equal(RouteState.Reconnecting, staleStarting.State);
    Equal(now.AddSeconds(1), staleStarting.RetryAt);
    var activeStarting = RapidStreamRecoveryPolicy.MarkConnectedPublisherRecoveryDue(
        RapidSavedRoute(RouteState.Starting, now), now.AddSeconds(1), ownsLiveProcess: true);
    Equal(RouteState.Starting, activeStarting.State);
}

static RuntimeRoute RapidSavedRoute(RouteState state, DateTimeOffset now) => new(
    SourceId: "WOWZA/live/_definst_/rapid.stream",
    SourceName: "Rapid stream",
    PortId: "PORT-1",
    PortName: "Output 1",
    PresetId: "1080p25",
    State: state,
    AssignmentMode: AssignmentMode.Manual,
    Locked: true,
    Priority: 0,
    RestartCount: 1,
    Frame: null,
    Fps: null,
    Speed: null,
    DroppedFrames: 0,
    DuplicatedFrames: 0,
    StartedAt: now,
    UpdatedAt: now,
    FailureCategory: "InputSessionLost",
    FailureMessage: "Rapid interruption",
    RetryAt: now,
    DesiredPortId: "PORT-1",
    DesiredPortName: "Output 1");

static void MissingLiveOwnerEntersRetryBeforeReservation()
{
    True(MissingRouteProcessRecoveryPolicy.RequiresRetry(true, RouteState.Running, false));
    True(MissingRouteProcessRecoveryPolicy.RequiresRetry(true, RouteState.Starting, false));
    True(!MissingRouteProcessRecoveryPolicy.RequiresRetry(true, RouteState.Running, true));
    True(!MissingRouteProcessRecoveryPolicy.RequiresRetry(true, RouteState.Fallback, false));
    True(!MissingRouteProcessRecoveryPolicy.RequiresRetry(false, RouteState.Running, false));
}

static void SourceIdentityIsUnambiguous()
{
    var id = new SourceIdentity("WOWZA-MAIN", "live", "_definst_", "tip/one.stream");
    Equal("WOWZA-MAIN/live/_definst_/tip%2Fone.stream", id.Value);
}

static void GeneratedIdsSkipCollisions()
{
    Equal("WOWZA-2", UniqueIdGenerator.Next("WOWZA", ["WOWZA-1", "WOWZA-3"]));
    Equal("preset-3", UniqueIdGenerator.Next("preset", ["preset-1", "PRESET-2"]));
}

static void DeckLinkOfficialSoftwareUpdateIsParsed()
{
    const string html = """
        <div class="sdk-download-info">
          <h4 class="file-download-title">Desktop Video 16.2</h4>
          <p class="release-date">Last Tuesday</p>
        </div>
        """;
    var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    var release = DeckLinkSoftwareInformationProvider.ParseOfficialReleasePage(html, now);
    Equal("16.2", release.Version);
    Equal(new DateOnly(2026, 7, 28), release.ReleasedOn!.Value);
    Equal(true, DeckLinkSoftwareInformationProvider.CompareVersions("16.1.0.0", release.Version));
    Equal(false, DeckLinkSoftwareInformationProvider.CompareVersions("16.2.0.0", release.Version));
    Equal<bool?>(null, DeckLinkSoftwareInformationProvider.CompareVersions("Not Available", release.Version));
    Throws<InvalidOperationException>(() => DeckLinkSoftwareInformationProvider.ParseOfficialReleasePage("<html></html>", now));
}

static void RenamedWowzaSourceIsPruned()
{
    var oldSource = Observed("OLD-ID", "news.stream");
    var renamedSource = Observed("NEW-ID", "news.stream");
    var stale = SourceObservationReconciler.FindStaleSources(
        [oldSource], [renamedSource], ["NEW-ID"], ["NEW-ID"], simulationMode: false);
    Equal(oldSource.Identity.Value, stale.Single().Identity.Value);
}

static void FailedWowzaPollRetainsSource()
{
    var existing = Observed("WOWZA-PROD", "news.stream");
    var stale = SourceObservationReconciler.FindStaleSources(
        [existing], [], ["WOWZA-PROD"], [], simulationMode: false);
    Equal(0, stale.Count);
}

static DiscoveredSource Observed(string serverId, string streamName) => new(
    new SourceIdentity(serverId, "live", "_definst_", streamName),
    streamName,
    new Uri($"rtsp://127.0.0.1:8698/live/{streamName}"),
    SourceState.Ready,
    100);

static void RtspUrlIsGeneratedAndEscaped()
{
    var server = Server("rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}");
    var uri = RtspUrlGenerator.Generate(server, new SourceIdentity("MAIN", "live", "_definst_", "tip stream.stream"));
    Equal("rtsp://127.0.0.1:8698/live/tip%20stream.stream", uri.AbsoluteUri);
}

static void InvalidRtspTokenIsRejected() => Throws<FormatException>(() => RtspUrlGenerator.ValidateTemplate("rtsp://{shell}/{stream-name}"));

static void DuplicateReservationIsPrevented()
{
    var manager = new PortReservationManager();
    var first = new SourceIdentity("A", "live", "_definst_", "one");
    var second = new SourceIdentity("A", "live", "_definst_", "two");
    True(manager.TryReserve("PORT-1", first, false, DateTimeOffset.UtcNow, out _));
    True(!manager.TryReserve("PORT-1", second, false, DateTimeOffset.UtcNow, out _));
}

static void ConcurrentReservationStressAllowsOneOwner()
{
    var manager = new PortReservationManager();
    var winners = 0;
    Parallel.For(0, 500, index =>
    {
        var source = new SourceIdentity("STRESS", "live", "_definst_", $"source-{index}");
        if (manager.TryReserve("PORT-1", source, false, DateTimeOffset.UtcNow, out _)) Interlocked.Increment(ref winners);
    });
    Equal(1, winners);
    Equal(1, manager.Snapshot().Count);
}

static void LockedReservationRequiresForce()
{
    var manager = new PortReservationManager();
    var source = new SourceIdentity("A", "live", "_definst_", "one");
    True(manager.TryReserve("PORT-1", source, true, DateTimeOffset.UtcNow, out _));
    True(!manager.Release("PORT-1", source));
    True(manager.Release("PORT-1", source, true));
}

static void ReservationReleaseDistinguishesMissingAndForeignOwnership()
{
    var manager = new PortReservationManager();
    var first = new SourceIdentity("A", "live", "_definst_", "one");
    var second = new SourceIdentity("A", "live", "_definst_", "two");

    Equal(PortReleaseResult.AlreadyFree, manager.ReleaseWithResult("PORT-1", first));
    True(manager.TryReserve("PORT-1", first, false, DateTimeOffset.UtcNow, out _));
    Equal(PortReleaseResult.OwnedByOther, manager.ReleaseWithResult("PORT-1", second, force: true));
    Equal(first, manager.Snapshot().Single().Source);
    Equal(PortReleaseResult.Released, manager.ReleaseWithResult("PORT-1", first));
}

static void StartupFailureReleasesReservation()
{
    var manager = new PortReservationManager();
    var source = new SourceIdentity("A", "live", "_definst_", "one");
    True(manager.TryReserve("PORT-1", source, true, DateTimeOffset.UtcNow, out _));
    var now = DateTimeOffset.UtcNow;
    var route = new RuntimeRoute(source.Value, "One", "PORT-1", "Output 1", "1080p25", RouteState.Starting,
        AssignmentMode.Manual, true, 100, 0, null, null, null, 0, 0, null, now, null, null);
    var failed = RouteStartFailureRecovery.ReleaseAndFail(manager, route, source, "Process could not start.", now);
    Equal(0, manager.Snapshot().Count);
    Equal(RouteState.Failed, failed.State);
    Equal<string?>(null, failed.PortId);
    Equal("ProcessStart", failed.FailureCategory);
}

static void MissingSourceLeaseRetentionHonorsLockAndGrace()
{
    var missing = DateTimeOffset.UtcNow;
    True(!RouteLeaseRetentionPolicy.ShouldRelease(false, missing, missing.AddSeconds(29), TimeSpan.FromSeconds(30)));
    True(RouteLeaseRetentionPolicy.ShouldRelease(false, missing, missing.AddSeconds(30), TimeSpan.FromSeconds(30)));
    True(!RouteLeaseRetentionPolicy.ShouldRelease(true, missing, missing.AddDays(1), TimeSpan.FromSeconds(30)));
    True(!RouteLeaseRetentionPolicy.IsStable(missing, missing.AddSeconds(4), TimeSpan.FromSeconds(5)));
    True(RouteLeaseRetentionPolicy.IsStable(missing, missing.AddSeconds(5), TimeSpan.FromSeconds(5)));
}

static void EmergencyStopBlocksRouteStarts()
{
    RouteControlSafety.EnsureStartAllowed(false);
    Throws<InvalidOperationException>(() => RouteControlSafety.EnsureStartAllowed(true));
}

static void LockedRouteStopIsRefused()
{
    RouteControlSafety.EnsureStopAllowed(false, false);
    RouteControlSafety.EnsureStopAllowed(true, true);
    Throws<InvalidOperationException>(() => RouteControlSafety.EnsureStopAllowed(true, false));
}

static void RouteCommandsRequireAdministrator()
{
    var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [new(System.Security.Claims.ClaimTypes.Role, "Administrator")], "test"));
    var readOnly = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [new(System.Security.Claims.ClaimTypes.Role, "Operator")], "test"));
    OperatorAuthorization.EnsureAdministrator(admin);
    Throws<UnauthorizedAccessException>(() => OperatorAuthorization.EnsureAdministrator(readOnly));
    Throws<UnauthorizedAccessException>(() => OperatorAuthorization.EnsureAdministrator(new System.Security.Claims.ClaimsPrincipal()));
}

static void HigherPriorityDequeuesFirst()
{
    var queue = new PriorityWaitingQueue();
    var low = new SourceIdentity("A", "live", "_definst_", "low");
    var high = new SourceIdentity("A", "live", "_definst_", "high");
    queue.Enqueue(low, 1, "busy");
    queue.Enqueue(high, 10, "busy");
    Equal(high, queue.Dequeue()!.Source);
}

static void InvalidStateJumpIsRejected()
{
    var route = new RouteRecord(new SourceIdentity("A", "live", "_definst_", "one"), null, "HD25", RouteState.Known, AssignmentMode.None, false);
    Throws<InvalidOperationException>(() => new RouteStateMachine().Transition(route, RouteState.Running));
}

static void RecoveryCanReacquireReservation()
{
    var machine = new RouteStateMachine();
    True(machine.CanTransition(RouteState.Fallback, RouteState.Reserved));
    True(machine.CanTransition(RouteState.Reconnecting, RouteState.Reserved));
    True(!machine.CanTransition(RouteState.Fallback, RouteState.Running));
}

static void RepeatedFailuresAreLogThrottled()
{
    var gate = new RepeatedFailureLogGate();
    var start = DateTimeOffset.Parse("2026-07-30T18:00:00Z");
    True(gate.Evaluate("Fallback -> Reserved", start, TimeSpan.FromMinutes(1)).ShouldLog);
    True(!gate.Evaluate("Fallback -> Reserved", start.AddSeconds(1), TimeSpan.FromMinutes(1)).ShouldLog);
    True(!gate.Evaluate("Fallback -> Reserved", start.AddSeconds(2), TimeSpan.FromMinutes(1)).ShouldLog);
    var repeated = gate.Evaluate("Fallback -> Reserved", start.AddMinutes(1), TimeSpan.FromMinutes(1));
    True(repeated.ShouldLog);
    Equal(2, repeated.SuppressedCount);
    True(gate.Evaluate("different failure", start.AddMinutes(1).AddSeconds(1), TimeSpan.FromMinutes(1)).ShouldLog);
    Equal(0, gate.Reset());
}

static void AutomaticAssignmentUsesOnePort()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result[0];
    var ports = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result;
    var manager = new PortReservationManager();
    var result = new AutomaticAssignmentEngine(manager, new PriorityWaitingQueue()).Assign(source, ports);
    True(result.Assigned);
    Equal(1, manager.Snapshot().Count);
}

static void AutomaticAssignmentIgnoresInputOnlyPorts()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result[0];
    var discovered = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result;
    var inputOnly = discovered[0] with { IsOutputPort = false };
    var output = discovered[1] with { IsOutputPort = true };
    var manager = new PortReservationManager();
    var result = new AutomaticAssignmentEngine(manager, new PriorityWaitingQueue()).Assign(source, [inputOnly, output]);
    True(result.Assigned);
    Equal(output.StableId, result.Port!.StableId);
    Equal(output.StableId, manager.Snapshot().Single().PortId);

    var none = new AutomaticAssignmentEngine(new PortReservationManager(), new PriorityWaitingQueue())
        .Assign(source, [inputOnly]);
    True(!none.Assigned);
}

static void SavedRoutingPriorityAndProtection()
{
    var now = DateTimeOffset.UtcNow;
    var manual = new RuntimeRoute("A/live/_definst_/manual", "Manual", null, null, "1080p25",
        RouteState.WaitingForStream, AssignmentMode.Manual, true, 10, 0, null, null, null, 0, 0, null, now,
        null, null, DesiredPortId: "PORT-1", ReserveWhileOffline: true, AllowTemporaryUse: false);
    var preconfigured = manual with { SourceId = "A/live/_definst_/preconfigured", AssignmentMode = AssignmentMode.Preconfigured };
    var temporary = manual with { AllowTemporaryUse = true };
    True(DesiredRoutePolicy.HasSavedAssignment(manual));
    True(DesiredRoutePolicy.ProtectsPortWhileOffline(manual));
    True(!DesiredRoutePolicy.ProtectsPortWhileOffline(temporary));
    True(DesiredRoutePolicy.PriorityRank(preconfigured.AssignmentMode) > DesiredRoutePolicy.PriorityRank(manual.AssignmentMode));
    True(DesiredRoutePolicy.PriorityRank(manual.AssignmentMode) > DesiredRoutePolicy.PriorityRank(AssignmentMode.Automatic));

    var legacy = manual with { PortId = "PORT-2", DesiredPortId = null, DesiredPortName = null };
    Equal("PORT-2", DesiredRoutePolicy.MigrateLegacy(legacy).DesiredPortId);
    var staleRunning = preconfigured with { PortId = "PORT-1", State = RouteState.Running, StartedAt = now.AddMinutes(-2), Frame = 3000 };
    var recovered = DesiredRoutePolicy.ResetTransientStateForStartup(staleRunning, now.AddSeconds(1));
    Equal(RouteState.WaitingForStream, recovered.State);
    Equal(null, recovered.PortId);
    Equal(null, recovered.Frame);
    Equal("PORT-1", recovered.DesiredPortId);
}

static void SimulationReturnsUsableVideo()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    True(source.Media!.HasUsableVideo);
    Equal(SourceState.Ready, source.State);
}

static void RetryPolicyCapsAtMaximum()
{
    var policy = RetryPolicy.BroadcastDefault;
    Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));
    Equal(TimeSpan.FromSeconds(30), policy.GetDelay(100));
}

static void RetryAttemptCapIsOptIn()
{
    True(!RetryLimitPolicy.IsExhausted(100, 0));
    True(!RetryLimitPolicy.IsExhausted(3, 3));
    True(RetryLimitPolicy.IsExhausted(4, 3));
}

static void StartupRouteRecoveryIsSingleShot()
{
    var tracker = new StartupRouteRecoveryTracker();
    tracker.Track("MAIN/live/_definst_/feed");
    True(tracker.IsPending("MAIN/live/_definst_/feed"));
    True(tracker.TryBegin("MAIN/live/_definst_/feed"));
    True(!tracker.IsPending("MAIN/live/_definst_/feed"));
    True(!tracker.TryBegin("MAIN/live/_definst_/feed"));
}

static void FfmpegCommandUsesArgumentList()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = new OutputPreset("HD25", "1080p25", new VideoMode(1920, 1080, 25, 1, "uyvy422"), true, 256);
    var start = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe", ReadTimeout: TimeSpan.FromSeconds(10)), source, port, preset);
    True(!start.UseShellExecute);
    True(start.ArgumentList.Contains("-progress"));
    True(start.ArgumentList.Contains(port.FfmpegName));
    True(start.ArgumentList.Contains("-timeout"));
    True(!start.ArgumentList.Contains("-rw_timeout"));
    Equal("10000000", start.ArgumentList[start.ArgumentList.IndexOf("-timeout") + 1]);
    True(start.ArgumentList.Contains("-fflags"));
    True(start.ArgumentList.Contains("nobuffer"));
    True(start.ArgumentList.Contains("-analyzeduration"));
    True(start.ArgumentList.Contains("1000000"));
    True(start.ArgumentList.Contains("-probesize"));
    True(start.ArgumentList.Contains("-fpsprobesize"));
    True(start.ArgumentList.IndexOf("-analyzeduration") < start.ArgumentList.IndexOf("-i"));
    True(start.ArgumentList.Contains("48000"));
    True(start.ArgumentList.Contains("pcm_s16le"));
    True(!start.ArgumentList.Contains("-win_safe_terminate"));

    var safeStart = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe",
        ReadTimeout: TimeSpan.FromSeconds(10), UseWindowsDeckLinkSafeTerminate: true), source, port, preset);
    var safeIndex = safeStart.ArgumentList.IndexOf("-win_safe_terminate");
    True(safeIndex >= 0);
    Equal("1", safeStart.ArgumentList[safeIndex + 1]);
    True(safeIndex < safeStart.ArgumentList.IndexOf("-f"));
}

static void FfmpegAudioLedRouteGeneratesBlackVideo()
{
    var source = new DiscoveredSource(
        new SourceIdentity("AUDIO", "live", "_definst_", "audio-led.stream"),
        "Audio-led source",
        new Uri("rtsp://127.0.0.1:8698/live/audio-led.stream"),
        SourceState.Ready,
        100,
        new MediaProperties("h264", "aac", 1920, 1080, 30, 1_000_000, 48_000, 2, false, false));
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = new OutputPreset("HD25", "1080p25", new VideoMode(1920, 1080, 25, 1, "uyvy422"), true, 256);
    var start = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe", ReadTimeout: TimeSpan.FromSeconds(10)), source, port, preset);

    True(start.ArgumentList.Any(argument => argument == "color=c=black:size=1920x1080:rate=25/1"));
    True(start.ArgumentList.Contains("1:v:0"));
    True(start.ArgumentList.Contains("0:a:0"));
    True(!start.ArgumentList.Contains("0:v:0"));
    True(start.ArgumentList.Contains("-shortest"));
    True(start.ArgumentList.Contains("pcm_s16le"));
    True(!start.ArgumentList.Contains("-b:v"));

    var noAudioPreset = preset with { IncludeAudio = false };
    Throws<InvalidOperationException>(() => FfmpegCommandBuilder.Build(
        new FfmpegRouteOptions("ffmpeg.exe"), source, port, noAudioPreset));
}

static void FfmpegInterlacedOutputIsExplicit()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = new OutputPreset("1080i50", "1080i50", new VideoMode(1920, 1080, 25, 1, "uyvy422"), true, 256, true, true);
    var start = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe", ReadTimeout: TimeSpan.FromSeconds(10)), source, port, preset);
    var filter = start.ArgumentList[start.ArgumentList.IndexOf("-vf") + 1];
    True(filter.Contains("fps=50/1", StringComparison.Ordinal));
    True(filter.Contains("tinterlace=interleave_top", StringComparison.Ordinal));
    True(filter.Contains("setfield=tff", StringComparison.Ordinal));

    var profile = OutputPresetProfile.CommonDefaults().Single(item => item.Id == "1080i50");
    True(profile.ToDomain().Interlaced);
}

static void FfmpegDisplayRedactsCredentials()
{
    var plain = new System.Diagnostics.ProcessStartInfo("ffmpeg.exe");
    plain.ArgumentList.Add("-i");
    plain.ArgumentList.Add("rtsp://operator:secret@example.test/live/feed");
    var display = FfmpegCommandBuilder.ToRedactedDisplay(plain);
    True(!display.Contains("secret", StringComparison.Ordinal));
    True(display.Contains("***", StringComparison.Ordinal));
}

static void BrowserPreviewIsTokenizedWithVuOverlay()
{
    var source = new DiscoveredSource(
        new SourceIdentity("MAIN", "live", "_definst_", "studio one"),
        "Studio Preview",
        new Uri("rtsp://127.0.0.1:8698/live/studio%20one"),
        SourceState.Ready,
        100,
        new MediaProperties("h264", "aac", 1920, 1080, 25, 5_000_000, 48_000, 2, true, false));
    var plan = BrowserPreviewCommandBuilder.Build(new MediaToolPaths
    {
        FfmpegPath = @"C:\Media\ffmpeg.exe"
    }, source);

    True(!plan.Producer.UseShellExecute);
    True(plan.Producer.ArgumentList.Contains(source.RtspUri.AbsoluteUri));
    True(plan.Producer.ArgumentList.Any(argument => argument.Contains("showvolume", StringComparison.Ordinal)));
    True(plan.Producer.ArgumentList.Any(argument => argument.Contains("overlay=10:414", StringComparison.Ordinal)));
    True(plan.Producer.ArgumentList.Any(argument => argument.Contains("scale=720:404", StringComparison.Ordinal)));
    True(plan.Producer.ArgumentList.Contains("libx264"));
    True(plan.Producer.ArgumentList.Contains("aac"));
    True(plan.Producer.ArgumentList.Contains("frag_keyframe+empty_moov+default_base_moof"));
    True(plan.Producer.ArgumentList.Contains("mp4") && plan.Producer.ArgumentList.Contains("pipe:1"));

    var videoOnly = source with { Media = source.Media! with { AudioCodec = null, AudioSampleRate = null, AudioChannels = null } };
    var videoOnlyPlan = BrowserPreviewCommandBuilder.Build(new MediaToolPaths
    {
        FfmpegPath = @"C:\Media\ffmpeg.exe"
    }, videoOnly);
    True(!videoOnlyPlan.Producer.ArgumentList.Any(argument => argument.Contains("showvolume", StringComparison.Ordinal)));
    True(videoOnlyPlan.Producer.ArgumentList.Contains("-an"));

    var audioLed = source with { Media = source.Media! with { HasUsableVideo = false } };
    var audioLedPlan = BrowserPreviewCommandBuilder.Build(new MediaToolPaths
    {
        FfmpegPath = @"C:\Media\ffmpeg.exe"
    }, audioLed);
    True(audioLedPlan.Producer.ArgumentList.Any(argument => argument.Contains("color=c=0x060b12", StringComparison.Ordinal)));
    True(audioLedPlan.Producer.ArgumentList.Any(argument => argument.Contains("[1:v:0]scale=720:404", StringComparison.Ordinal)));
    True(audioLedPlan.Producer.ArgumentList.Contains("-shortest"));
}

static void FfmpegProgressIsParsed()
{
    var parser = new FfmpegProgressParser();
    var now = DateTimeOffset.UtcNow;
    parser.Accept("frame=125", now);
    parser.Accept("fps=25.0", now);
    parser.Accept("out_time_us=5000000", now);
    parser.Accept("drop_frames=2", now);
    parser.Accept("dup_frames=1", now);
    parser.Accept("speed=1.01x", now);
    var progress = parser.Accept("progress=continue", now)!;
    Equal(125L, progress.Frame);
    Equal(TimeSpan.FromSeconds(5), progress.OutputTime);
    Equal(2L, progress.DroppedFrames);
}

static void FfmpegStallIsDetected()
{
    var now = DateTimeOffset.UtcNow;
    var progress = new FfmpegProgressSnapshot(10, 25, TimeSpan.FromSeconds(1), 0, 0, 1, now.AddSeconds(-11), false);
    True(FfmpegStallDetector.IsStalled(true, progress, now, TimeSpan.FromSeconds(10)));
    True(!FfmpegStallDetector.IsStalled(false, progress, now, TimeSpan.FromSeconds(10)));
}

static void FfmpegDuplicateFrameFreezeIsDetected()
{
    var detector = new FfmpegInputFreezeDetector();
    var now = DateTimeOffset.UtcNow;
    var initial = new FfmpegProgressSnapshot(100, 25, TimeSpan.FromSeconds(4), 0, 0, 1, now, false);
    True(!detector.Observe(101, initial, now, TimeSpan.FromSeconds(8), allowRapidBurst: false));
    True(!detector.Observe(101, initial with { Frame = 125, DuplicatedFrames = 25 }, now.AddSeconds(1), TimeSpan.FromSeconds(8), allowRapidBurst: false));
    True(!detector.Observe(101, initial with { Frame = 300, DuplicatedFrames = 200 }, now.AddSeconds(8), TimeSpan.FromSeconds(8), allowRapidBurst: false));
    True(detector.Observe(101, initial with { Frame = 325, DuplicatedFrames = 225 }, now.AddSeconds(9), TimeSpan.FromSeconds(8), allowRapidBurst: false));

    detector.Reset();
    True(!detector.Observe(102, initial, now, TimeSpan.FromSeconds(8)));
    True(!detector.Observe(102, initial with { Frame = 125, DuplicatedFrames = 1 }, now.AddSeconds(10), TimeSpan.FromSeconds(8)));
    True(!detector.Observe(103, initial with { Frame = 150, DuplicatedFrames = 50 }, now.AddSeconds(20), TimeSpan.FromSeconds(8)));
}

static void FfmpegRapidDuplicateBurstIsDetected()
{
    var detector = new FfmpegInputFreezeDetector();
    var now = DateTimeOffset.UtcNow;
    var initial = new FfmpegProgressSnapshot(100, 25, TimeSpan.FromSeconds(4), 0, 0, 1, now, false);
    True(!detector.Observe(201, initial, now, TimeSpan.FromSeconds(8)));
    True(detector.Observe(201, initial with { Frame = 115, DuplicatedFrames = 15 },
        now.AddMilliseconds(500), TimeSpan.FromSeconds(8)));

    detector.Reset();
    True(!detector.Observe(202, initial, now, TimeSpan.FromSeconds(8), allowRapidBurst: false));
    True(!detector.Observe(202, initial with { Frame = 125, DuplicatedFrames = 25 },
        now.AddSeconds(1), TimeSpan.FromSeconds(8), allowRapidBurst: false));
}

static void FfmpegFirstProgressTimeoutIsDetected()
{
    var now = DateTimeOffset.UtcNow;
    True(FfmpegStallDetector.IsFirstProgressTimedOut(true, null, now.AddSeconds(-21), now, TimeSpan.FromSeconds(20)));
    True(!FfmpegStallDetector.IsFirstProgressTimedOut(true, null, now.AddSeconds(-19), now, TimeSpan.FromSeconds(20)));
    var progress = new FfmpegProgressSnapshot(1, 25, TimeSpan.Zero, 0, 0, 1, now, false);
    True(!FfmpegStallDetector.IsFirstProgressTimedOut(true, progress, now.AddMinutes(-1), now, TimeSpan.FromSeconds(20)));
    var noFrames = progress with { Frame = 0 };
    True(FfmpegStallDetector.IsFirstProgressTimedOut(true, noFrames, now.AddSeconds(-21), now, TimeSpan.FromSeconds(20)));
}

static void CoordinatorLivenessDetectsBlockedCycle()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = new CoordinatorLivenessSnapshot(
        now.AddHours(-1), now.AddSeconds(-119), now.AddSeconds(-120), "Standby reconciliation", 1_000);
    True(CoordinatorLivenessPolicy.IsResponsive(snapshot, now, TimeSpan.FromMinutes(2)));
    True(!CoordinatorLivenessPolicy.IsResponsive(snapshot with { LastProgressAt = now.AddSeconds(-121) },
        now, TimeSpan.FromMinutes(2)));
    True(CoordinatorLivenessPolicy.IsResponsive(snapshot with { LastProgressAt = now.AddSeconds(1) },
        now, TimeSpan.FromMinutes(2)));
    Throws<ArgumentOutOfRangeException>(() => CoordinatorLivenessPolicy.IsResponsive(snapshot, now, TimeSpan.Zero));
}

static void CoordinatorWatchdogIsRegisteredAndHealthAware()
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var program = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Web", "Program.cs"));
    var watchdog = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Web", "Services", "RouterCoordinatorWatchdog.cs"));
    var supervisor = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Infrastructure", "FfmpegProcessSupervisor.cs"));
    var commands = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Infrastructure", "ExternalCommandRunner.cs"));

    True(program.Contains("AddHostedService<RouterCoordinatorWatchdog>()", StringComparison.Ordinal));
    True(program.Contains("CoordinatorLivenessPolicy.IsResponsive", StringComparison.Ordinal));
    True(watchdog.Contains("Environment.FailFast", StringComparison.Ordinal));
    True(watchdog.Contains("CoordinatorWatchdog", StringComparison.Ordinal));
    True(supervisor.Contains("ProcessReapTimeout", StringComparison.Ordinal));
    True(supervisor.Contains("StreamDrainTimeout", StringComparison.Ordinal));
    True(!commands.Contains("WaitForExitAsync(CancellationToken.None)", StringComparison.Ordinal));
}

static void DeckLinkIdentityPollingIsIsolatedAndBounded()
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var coordinator = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Web", "Services", "RouterCoordinator.cs"));
    var program = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Web", "Program.cs"));
    var probe = File.ReadAllText(Path.Combine(root, "src", "BroadcastRouter.Infrastructure", "DeckLinkIdentityProcessProbe.cs"));

    True(coordinator.Contains("DeckLinkIdentityProcessProbe.EnumerateAsync", StringComparison.Ordinal));
    True(!coordinator.Contains("DeckLinkSdkIdentityEnumerator.Enumerate()", StringComparison.Ordinal));
    True(program.Contains("DeckLinkIdentityProcessProbe.CommandArgument", StringComparison.Ordinal));
    True(probe.Contains("DefaultTimeout", StringComparison.Ordinal));
    True(probe.Contains("containOnWindows: true", StringComparison.Ordinal));
}

static void WindowsJobKillsOrphanedProcess()
{
    if (!OperatingSystem.IsWindows()) return;

    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c ping 127.0.0.1 -n 30 > nul")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("Containment test process did not start.");

    using (var job = WindowsKillOnCloseJob.Create())
    {
        job.Add(process);
        True(!process.HasExited);
    }

    True(process.WaitForExit(5000));
}

static void FaultedSnapshotSubscriberDoesNotBlockOthers()
{
    var calls = new List<string>();
    Action handlers = () => calls.Add("first");
    handlers += () => throw new InvalidOperationException("disposed circuit");
    handlers += () => calls.Add("last");

    var failures = ChangeNotificationDispatcher.Dispatch(handlers);

    Equal(2, calls.Count);
    Equal("first", calls[0]);
    Equal("last", calls[1]);
    Equal(1, failures.Count);
}

static void RouteTelemetryUpdatesSkipPersistence()
{
    var route = PersistencePolicyRoute();
    var telemetry = route with
    {
        Frame = 2500,
        Fps = 25,
        Speed = 1,
        DroppedFrames = 3,
        DuplicatedFrames = 4,
        UpdatedAt = route.UpdatedAt.AddSeconds(1)
    };
    True(!RouteTelemetryPersistencePolicy.RequiresPersistence(route, telemetry));
}

static void DurableRouteChangesRequirePersistence()
{
    var route = PersistencePolicyRoute();
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { State = RouteState.Reconnecting }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { PortId = "PORT-B" }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { PresetId = "HD50" }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { Locked = true }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { RestartCount = 1 }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { FailureCategory = "Network" }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { RetryAt = route.UpdatedAt.AddSeconds(2) }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { DesiredPortId = "PORT-B" }));
    True(RouteTelemetryPersistencePolicy.RequiresPersistence(route, route with { ReserveWhileOffline = false }));
}

static RuntimeRoute PersistencePolicyRoute() => new(
    "SIM/live/_definst_/policy.stream", "policy.stream", "PORT-A", "Output A", "HD25",
    RouteState.Running, AssignmentMode.Manual, false, 10, 0, 100, 25, 1, 0, 0,
    DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
    null, null, DesiredPortId: "PORT-A", DesiredPortName: "Output A");

static void FfmpegVideoOnlyRouteGeneratesSilence()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    source = source with
    {
        Media = source.Media! with
        {
            AudioCodec = null,
            AudioSampleRate = null,
            AudioChannels = null
        }
    };
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = new OutputPreset("HD25", "1080p25", new VideoMode(1920, 1080, 25, 1, "uyvy422"), true, 256);
    var start = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe"), source, port, preset);
    True(start.ArgumentList.Contains("anullsrc=r=48000:cl=stereo"));
    True(start.ArgumentList.Contains("1:a:0"));
    True(!start.ArgumentList.Contains("0:a:0?"));
    var audioFilter = start.ArgumentList[start.ArgumentList.IndexOf("-af") + 1];
    True(audioFilter.Contains("volume=0", StringComparison.Ordinal));
}

static void OwnedProcessStopAndRestartAreSerialized()
{
    if (!OperatingSystem.IsWindows()) return;

    var owner = new SourceIdentity("SYSTEM", "test", "_definst_", "serialized-owner");
    var lifecycle = new System.Collections.Concurrent.ConcurrentQueue<RouteProcessLifecycleEvent>();
    var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused.exe"), TimeSpan.FromSeconds(2));
    supervisor.LifecycleChanged += lifecycle.Enqueue;
    try
    {
        supervisor.StartOwnedProcessForTestingAsync(owner, SlowExitProcess()).GetAwaiter().GetResult();
        var firstPid = supervisor.Snapshot().Single(value => value.Source == owner && value.Running).ProcessId;

        var stop = supervisor.StopAsync(owner, CancellationToken.None);
        Thread.Sleep(75);
        var restart = supervisor.StartOwnedProcessForTestingAsync(owner, SlowExitProcess());
        Thread.Sleep(75);
        True(!restart.IsCompleted);

        stop.GetAwaiter().GetResult();
        restart.GetAwaiter().GetResult();
        var secondPid = supervisor.Snapshot().Single(value => value.Source == owner && value.Running).ProcessId;
        True(secondPid != firstPid);
        supervisor.StopAsync(owner, CancellationToken.None).GetAwaiter().GetResult();
        Equal(2, lifecycle.Count(value => value.State == RouteProcessLifecycleState.Exited));
    }
    finally { supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

    static System.Diagnostics.ProcessStartInfo SlowExitProcess()
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::Out.WriteLine('ready'); [Console]::In.ReadLine() | Out-Null; Start-Sleep -Milliseconds 600");
        return start;
    }
}

static void FallbackOwnershipIsReapedBeforeLiveRecovery()
{
    if (!OperatingSystem.IsWindows()) return;

    var owner = new SourceIdentity("SYSTEM", "test", "_definst_", "fallback-handoff-owner");
    var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused.exe"), TimeSpan.FromSeconds(2));
    try
    {
        supervisor.StartOwnedProcessForTestingAsync(owner, WaitingProcess(),
            purpose: RouteProcessPurpose.Fallback).GetAwaiter().GetResult();
        var fallback = supervisor.Snapshot().Single(value => value.Source == owner && value.Running);
        Equal(RouteProcessPurpose.Fallback, fallback.Purpose);

        supervisor.StopForOutputHandoffAsync(owner, CancellationToken.None).GetAwaiter().GetResult();
        supervisor.StartOwnedProcessForTestingAsync(owner, WaitingProcess()).GetAwaiter().GetResult();

        var live = supervisor.Snapshot().Single(value => value.Source == owner && value.Running);
        Equal(RouteProcessPurpose.Live, live.Purpose);
        True(live.ProcessId != fallback.ProcessId);
    }
    finally { supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

    static System.Diagnostics.ProcessStartInfo WaitingProcess()
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::Out.WriteLine('frame=1'); [Console]::In.ReadLine() | Out-Null");
        return start;
    }
}

static void UncooperativeOwnedProcessCleanupIsBounded()
{
    if (!OperatingSystem.IsWindows()) return;

    var owner = new SourceIdentity("SYSTEM", "test", "_definst_", "uncooperative-owner");
    var supervisor = new FfmpegProcessSupervisor(new FfmpegRouteOptions("unused.exe"), TimeSpan.FromMilliseconds(100));
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("while ($true) { [Console]::Out.WriteLine('frame=1'); [Console]::Error.WriteLine('noise'); Start-Sleep -Milliseconds 5 }");
        supervisor.StartOwnedProcessForTestingAsync(owner, start).GetAwaiter().GetResult();
        var processId = supervisor.Snapshot().Single(value => value.Source == owner && value.Running).ProcessId;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        supervisor.StopAsync(owner, CancellationToken.None).GetAwaiter().GetResult();
        elapsed.Stop();
        True(elapsed.Elapsed < TimeSpan.FromSeconds(7));
        True(!supervisor.Snapshot().Any(value => value.Source == owner && value.Running));
        try { True(System.Diagnostics.Process.GetProcessById(processId).HasExited); }
        catch (ArgumentException) { }
    }
    finally { supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void FfmpegFailureIsClassified()
{
    Equal(FfmpegFailureCategory.Authentication, FfmpegErrorClassifier.Classify(1, "RTSP server returned 401 Unauthorized"));
    Equal(FfmpegFailureCategory.DeckLinkBusy, FfmpegErrorClassifier.Classify(1, "Device or resource busy"));
    Equal(FfmpegFailureCategory.None, FfmpegErrorClassifier.Classify(0, ""));
}

static void DeckLinkOutputFailureIsClassified()
{
    const string headerFailure = "[out#0/decklink] Could not write header: I/O error";
    Equal(FfmpegFailureCategory.DeckLinkInitialization, FfmpegErrorClassifier.Classify(1, headerFailure));
    Equal(FfmpegFailureCategory.DeckLinkInitialization,
        FfmpegErrorClassifier.Classify(0, "[decklink] There are not enough buffered video frames. Video may misbehave!"));
    Equal(FfmpegFailureCategory.DeckLinkReference,
        FfmpegErrorClassifier.Classify(null, "[decklink] Genlock reference signal is not locked"));
    Equal(FfmpegFailureCategory.Network, FfmpegErrorClassifier.Classify(1, "RTSP connection timed out: I/O error"));
}

static void FfmpegRtspProtocolLossIsRetryable()
{
    const string protocolLoss = "[in#0/rtsp] CSeq 96 expected, 0 received.";
    True(FfmpegInputFailureDetector.TryClassify(protocolLoss, out var category));
    Equal("RtspProtocolDesynchronized", category);
    Equal(FfmpegFailureCategory.Network, FfmpegErrorClassifier.Classify(null,
        "[decklink] There's no buffered audio. Audio will misbehave! | " + protocolLoss));
    True(!FfmpegInputFailureDetector.TryClassify(
        "[decklink] There's no buffered audio. Audio will misbehave!", out _));
}

static void SupervisorCapturesRtspProtocolLoss()
{
    var supervisor = new FfmpegProcessSupervisor(
        new FfmpegRouteOptions("ffmpeg.exe"), TimeSpan.FromMilliseconds(250));
    var owner = new SourceIdentity("SIM", "live", "_definst_", $"rtsp-loss-{Guid.NewGuid():N}");
    var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("-NoProfile");
    start.ArgumentList.Add("-Command");
    start.ArgumentList.Add("[Console]::Error.WriteLine('[in#0/rtsp] CSeq 96 expected, 0 received.'); "
        + "[Console]::Out.WriteLine('frame=1'); [Console]::Out.WriteLine('progress=continue'); Start-Sleep -Seconds 15");

    try
    {
        supervisor.StartOwnedProcessForTestingAsync(owner, start).GetAwaiter().GetResult();
        RouteProcessSnapshot? snapshot = null;
        var captured = SpinWait.SpinUntil(() =>
        {
            snapshot = supervisor.Snapshot().SingleOrDefault(value => value.Source == owner);
            return snapshot?.InputFailure is not null;
        }, TimeSpan.FromSeconds(10));

        True(captured);
        True(snapshot is not null);
        True(snapshot!.InputFailure is not null);
        Equal("RtspProtocolDesynchronized", snapshot!.InputFailure!.Category);
        True(snapshot.InputFailure.Detail.Contains("CSeq 96 expected, 0 received", StringComparison.Ordinal));
    }
    finally
    {
        supervisor.StopAsync(owner, CancellationToken.None).GetAwaiter().GetResult();
        supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void PostStartupDeckLinkMediaStarvationIsDetected()
{
    var detector = new FfmpegMediaStarvationDetector();
    var startedAt = DateTimeOffset.UtcNow;
    var observedAt = startedAt.AddSeconds(30);
    True(detector.Observe("[decklink] There's no buffered audio. Audio will misbehave!",
        observedAt, startedAt, TimeSpan.FromSeconds(5), out var audioCategory, out _));
    Equal("DeckLinkAudioStarved", audioCategory);
    True(detector.Observe("[decklink] There are not enough buffered video frames. Video may misbehave!",
        observedAt, startedAt, TimeSpan.FromSeconds(5), out var videoCategory, out _));
    Equal("DeckLinkVideoStarved", videoCategory);
}

static void DeckLinkStartupStarvationIsIgnored()
{
    var detector = new FfmpegMediaStarvationDetector();
    var startedAt = DateTimeOffset.UtcNow;
    True(!detector.Observe("[decklink] There are not enough buffered video frames. Video may misbehave!",
        startedAt.AddSeconds(1), startedAt, TimeSpan.FromSeconds(5), out _, out _));
    True(!detector.Observe("[decklink] There's no buffered audio. Audio will misbehave!",
        startedAt.AddSeconds(1.2), startedAt, TimeSpan.FromSeconds(5), out _, out _));
}

static void SupervisorCapturesDeckLinkMediaStarvation()
{
    var supervisor = new FfmpegProcessSupervisor(
        new FfmpegRouteOptions("ffmpeg.exe"), TimeSpan.FromMilliseconds(250));
    var owner = new SourceIdentity("SIM", "live", "_definst_", $"decklink-starvation-{Guid.NewGuid():N}");
    var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("-NoProfile");
    start.ArgumentList.Add("-Command");
    start.ArgumentList.Add("[Console]::Out.WriteLine('frame=1'); [Console]::Out.WriteLine('progress=continue'); "
        + "Start-Sleep -Seconds 6; "
        + "[Console]::Error.WriteLine('[decklink] There are not enough buffered video frames. Video may misbehave!'); "
        + "[Console]::Error.WriteLine(\"[decklink] There's no buffered audio. Audio will misbehave!\"); "
        + "Start-Sleep -Seconds 15");

    try
    {
        supervisor.StartOwnedProcessForTestingAsync(owner, start).GetAwaiter().GetResult();
        RouteProcessSnapshot? snapshot = null;
        var captured = SpinWait.SpinUntil(() =>
        {
            snapshot = supervisor.Snapshot().SingleOrDefault(value => value.Source == owner);
            return snapshot?.InputFailure is not null;
        }, TimeSpan.FromSeconds(12));

        True(captured);
        Equal("DeckLinkVideoStarved", snapshot!.InputFailure!.Category);
    }
    finally
    {
        supervisor.StopAsync(owner, CancellationToken.None).GetAwaiter().GetResult();
        supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void FfprobeMediaIsParsed()
{
    const string json = """
    {"streams":[
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1","bit_rate":"3000000","nb_read_frames":"50","field_order":"progressive"},
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}
    ],"format":{"bit_rate":"3200000"}}
    """;
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened && result.FramesReceived);
    Equal("h264", result.Media!.VideoCodec);
    Equal(25d, result.Media.FramesPerSecond!.Value);
    Equal(48_000, result.Media.AudioSampleRate!.Value);
    Equal(false, result.Media.Interlaced!.Value);
}

static void FfprobeScanTypeIsParsed()
{
    const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1","nb_read_frames":"5","field_order":"tt"}]}""";
    Equal(true, FfprobeStreamProbe.Parse(json).Media!.Interlaced!.Value);
}

static void FfprobeAcceptsAudioLedSparseVideo()
{
    const string json = """
    {"streams":[
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30/1","nb_read_frames":"N/A","nb_read_packets":"0","field_order":"progressive"},
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_frames":"N/A","nb_read_packets":"94"}
    ]}
    """;
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened);
    True(!result.FramesReceived);
    True(result.AudioReceived);
    Equal(null, result.FailureCategory);
    True(!result.Media!.HasUsableVideo);
    Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(result));
}

static void FfprobeAcceptsDelayedContinuousVideo()
{
    const string json = """
    {"streams":[
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1","nb_read_frames":"5","field_order":"progressive"},
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_packets":"90"}
    ]}
    """;
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened && result.FramesReceived && result.AudioReceived);
    True(result.Media!.HasUsableVideo);
}

static void AdaptiveProbePromotesLongGopVideo()
{
    const string quickJson = """
    {"streams":[
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_frames":"90","nb_read_packets":"90"},
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p","avg_frame_rate":"30/1","nb_read_frames":"1","nb_read_packets":"1"}
    ]}
    """;
    const string extendedJson = """
    {"streams":[
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_frames":"559","nb_read_packets":"559"},
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"pix_fmt":"yuv420p","avg_frame_rate":"30/1","nb_read_frames":"137","nb_read_packets":"137"}
    ]}
    """;

    var quick = FfprobeStreamProbe.Parse(quickJson);
    var extended = FfprobeStreamProbe.Parse(extendedJson);
    True(SourceProbeReadinessPolicy.NeedsExtendedVideoConfirmation(quick));
    var selected = SourceProbeReadinessPolicy.PreferExtendedVideoEvidence(quick, extended);
    True(selected.FramesReceived && selected.Media!.HasUsableVideo);

    var audioLed = new SourceMediaModeState(SourceMediaMode.AudioLed, 0, 0, null);
    var promoted = SourceProbeReadinessPolicy.ObserveMediaMode(audioLed, selected, videoConfirmationProbes: 1);
    Equal(SourceMediaMode.Video, promoted.State.Mode);
    True(promoted.ModeChanged);

    var audioOnly = FfprobeStreamProbe.Parse(
        """{"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_packets":"80"}]}""");
    True(!SourceProbeReadinessPolicy.NeedsExtendedVideoConfirmation(audioOnly));
    Equal(quick, SourceProbeReadinessPolicy.PreferExtendedVideoEvidence(quick, quick));
}

static void FfprobeIgnoresIsolatedVideoFrames()
{
    const string json = """
    {"streams":[
      {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30/1","nb_read_frames":"1","field_order":"progressive"},
      {"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_packets":"96"}
    ]}
    """;
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened && result.AudioReceived && !result.FramesReceived);
    True(!result.Media!.HasUsableVideo);
    Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(result));
}

static void FfprobeAcceptsAudioOnlyInput()
{
    const string json = """{"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"nb_read_packets":"80"}]}""";
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened && result.AudioReceived && !result.FramesReceived);
    Equal(null, result.Media!.VideoCodec);
    Equal("aac", result.Media.AudioCodec);
    Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(result));
}

static void FfprobeRequiresReadFrames()
{
    const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"nb_read_frames":"N/A"}]}""";
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened);
    True(!result.FramesReceived);
    Equal("NoVideoFrames", result.FailureCategory);
}

static void ProbeReadinessAcceptsAudioAndRejectsMetadataOnly()
{
    var media = new MediaProperties("h264", "aac", 1920, 1080, 30, null, 48_000, 2, false);
    Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(new(true, false, media, null, null, true)));
    Equal(SourceState.UnsupportedMedia, SourceProbeReadinessPolicy.Resolve(new(true, false, media, "NoVideoFrames", null)));
    Equal(SourceState.RtspUnavailable, SourceProbeReadinessPolicy.Resolve(new(false, false, null, "Network", null)));
}

static void ProbeReadinessRetainsAudioLedMode()
{
    var media = new MediaProperties("h264", "aac", 1920, 1080, 30, null, 48_000, 2, true);
    var current = new StreamProbeResult(true, true, media, null, "Received sustained video.", true);
    var retained = SourceProbeReadinessPolicy.RetainAudioLedMode(current, previouslyAudioLed: true);
    True(retained.Opened && retained.AudioReceived && !retained.FramesReceived);
    True(!retained.Media!.HasUsableVideo);
    Equal(SourceState.Ready, SourceProbeReadinessPolicy.Resolve(retained));
}

static void ProbeReadinessRestoresStableVideo()
{
    True(!SourceProbeReadinessPolicy.ShouldRestoreVideo(1));
    True(SourceProbeReadinessPolicy.ShouldRestoreVideo(2));
}

static void ProbeMediaModeResistsFlapping()
{
    var videoMedia = new MediaProperties("h264", "aac", 1920, 1080, 25, null, 48_000, 2, true);
    var audioLedMedia = videoMedia with { HasUsableVideo = false };
    var video = new StreamProbeResult(true, true, videoMedia, null, "continuous video", true);
    var sparse = new StreamProbeResult(true, false, audioLedMedia, null, "audio with sparse video", true);

    var state = SourceMediaModeState.Unknown;
    var initial = SourceProbeReadinessPolicy.ObserveMediaMode(state, sparse);
    Equal(SourceMediaMode.AudioLed, initial.State.Mode);
    state = initial.State;

    var firstVideo = SourceProbeReadinessPolicy.ObserveMediaMode(state, video);
    Equal(SourceMediaMode.AudioLed, firstVideo.State.Mode);
    True(!firstVideo.EffectiveProbe.Media!.HasUsableVideo);
    state = firstVideo.State;

    var restored = SourceProbeReadinessPolicy.ObserveMediaMode(state, video);
    Equal(SourceMediaMode.Video, restored.State.Mode);
    True(restored.ModeChanged && restored.EffectiveProbe.Media!.HasUsableVideo);
    state = restored.State;

    var oneSparse = SourceProbeReadinessPolicy.ObserveMediaMode(state, sparse);
    Equal(SourceMediaMode.Video, oneSparse.State.Mode);
    True(oneSparse.EffectiveProbe.Media!.HasUsableVideo);
    True(!oneSparse.ModeChanged);
    state = oneSparse.State;

    var healthyAgain = SourceProbeReadinessPolicy.ObserveMediaMode(state, video);
    Equal(SourceMediaMode.Video, healthyAgain.State.Mode);
    Equal(0, healthyAgain.State.ConsecutiveAudioLedProbes);

    state = SourceProbeReadinessPolicy.ObserveMediaMode(healthyAgain.State, sparse).State;
    state = SourceProbeReadinessPolicy.ObserveMediaMode(state, sparse).State;
    var confirmedAudioLed = SourceProbeReadinessPolicy.ObserveMediaMode(state, sparse);
    Equal(SourceMediaMode.AudioLed, confirmedAudioLed.State.Mode);
    True(confirmedAudioLed.ModeChanged);
}

static void FfprobeRejectsMalformedOutput()
{
    Equal("InvalidOutput", FfprobeStreamProbe.Parse("").FailureCategory);
    Equal("InvalidOutput", FfprobeStreamProbe.Parse("{not-json").FailureCategory);
}

static void WowzaIncomingStreamsAreParsed()
{
    const string json = """
    {"serverName":"_defaultServer_","incomingStreams":[
      {"name":"tip.stream","isConnected":true,"sourceIp":"10.0.0.5","uptime":42},
      {"name":"offline.stream","isConnected":false}
    ]}
    """;
    using var document = System.Text.Json.JsonDocument.Parse(json);
    var streams = WowzaIncomingStreamParser.Parse(document.RootElement);
    Equal(2, streams.Count);
    Equal("tip.stream", streams[0].StreamName);
    True(streams[0].PublisherConnected);
    True(!streams[1].PublisherConnected);
}

static void WowzaDiscoveryRetainsDisconnectedStreams()
{
    const string json = """{"incomingStreams":[{"name":"active.stream","isConnected":true},{"name":"offline.stream","isConnected":false}]}""";
    using var client = new HttpClient(new StaticJsonHandler(json));
    var provider = new WowzaDiscoveryProvider(client, Server("rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}"),
        new StaticCredentialResolver(new CredentialValue("operator", "temporary")));
    var sources = provider.DiscoverAsync(default).GetAwaiter().GetResult();
    Equal(2, sources.Count);
    Equal(SourceState.PublisherActive, sources.Single(source => source.Identity.StreamName == "active.stream").State);
    Equal(SourceState.PublisherDisconnected, sources.Single(source => source.Identity.StreamName == "offline.stream").State);
}

static void AuthenticatedUrisAreRedacted()
{
    var result = LogRedactor.Redact("opening rtsp://operator:secret@10.0.0.1/live/feed and https://admin:password@example.test failed");
    True(!result.Contains("operator", StringComparison.Ordinal));
    True(!result.Contains("secret", StringComparison.Ordinal));
    True(!result.Contains("password", StringComparison.Ordinal));
    True(!result.Contains("10.0.0.1", StringComparison.Ordinal));
    True(result.Contains("rtsp://***:***@", StringComparison.Ordinal));
}

static void DiagnosticsOmitSensitiveData()
{
    var secretUri = new Uri("rtsp://operator:secret@10.20.30.40/live/customer-stream");
    var source = new DiscoveredSource(new SourceIdentity("INTERNAL-WOWZA", "live", "_definst_", "customer-stream"),
        "Customer Stream", secretUri, SourceState.Ready, 100);
    var route = new RuntimeRoute(source.Identity.Value, source.FriendlyName, "PORT-SECRET", "Transmission Secret", "1080p25",
        RouteState.Failed, AssignmentMode.Fixed, false, 100, 1, null, null, null, 0, 0, null, DateTimeOffset.UtcNow,
        "Network", $"Failed {secretUri}");
    var snapshot = new RouterSnapshot([source], [], [route], [],
        [new ServerHealth("INTERNAL-WOWZA", "Secret server", false, false, 0, "Failed http://10.20.30.40:8087/", DateTimeOffset.UtcNow)],
        MediaToolValidation.NotConfigured, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, false, false, "Running");
    var settings = new OperatorSettings
    {
        MediaTools = new() { FfmpegPath = @"C:\Secret\ffmpeg.exe" },
        WowzaServers = [new() { ManagementUrl = "http://10.20.30.40:8087/", Username = "admin", ProtectedPassword = "DPAPI-CIPHERTEXT" }],
        ManualSources = [new() { RtspUrl = secretUri.AbsoluteUri }]
    };
    var logs = new[] { new StructuredLogEntry(1, DateTimeOffset.UtcNow, "Error", "RTSP", $"Failed {secretUri}", source.Identity.Value, "abc") };
    var json = System.Text.Json.JsonSerializer.Serialize(new
    {
        Snapshot = DiagnosticSanitizer.SanitizeSnapshot(snapshot),
        Settings = DiagnosticSanitizer.SanitizeSettings(settings),
        Logs = DiagnosticSanitizer.SanitizeLogs(logs)
    });
    foreach (var forbidden in new[] { "secret", "10.20.30.40", "INTERNAL-WOWZA", "customer-stream", "DPAPI-CIPHERTEXT", @"C:\Secret" })
        if (json.Contains(forbidden, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Diagnostics still contain '{forbidden}'.");
}

static void DpapiCredentialRoundTrips()
{
    const string secret = "test-password-42";
    var protectedValue = WindowsDpapi.Protect(secret);
    True(!protectedValue.Contains(secret, StringComparison.Ordinal));
    Equal(secret, WindowsDpapi.Unprotect(protectedValue));
}

static void OperatorSettingsPersistAtomically()
{
    var path = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-test-{Guid.NewGuid():N}.json");
    try
    {
        var settings = new OperatorSettings
        {
            MediaTools = new MediaToolPaths { FfmpegPath = @"C:\media\ffmpeg.exe" },
            WowzaServers = [new WowzaServerProfile { FriendlyName = "Test Wowza", ProtectedPassword = WindowsDpapi.Protect("secret") }]
        };
        var store = new OperatorSettingsStore(path);
        store.SaveAsync(settings).GetAwaiter().GetResult();
        var loaded = store.LoadAsync().GetAwaiter().GetResult();
        Equal("Test Wowza", loaded.WowzaServers.Single().FriendlyName);
        Equal("secret", WindowsDpapi.Unprotect(loaded.WowzaServers.Single().ProtectedPassword));
        Equal(@"C:\media\ffmpeg.exe", loaded.MediaTools.FfmpegPath);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }
}

static void DeckLinkSinksAreParsed()
{
    const string output = """
    Auto-detected sinks for decklink:
      80:a1b2c3d0:00000000 [DeckLink Quad (1)] (none)
      80:a1b2c3d1:00000000 [DeckLink Quad (2)] (none)
      80:a1b2c3d4:00000000 [DeckLink Quad (5)] (none)
    """;
    var devices = FfmpegDiagnostics.ParseDeckLinkSinks(output);
    Equal(3, devices.Count);
    Equal("80:a1b2c3d0:00000000", devices[0].FfmpegAddress);
    Equal("DeckLink Quad (1)", devices[0].DisplayName);
    Equal("DeckLink Quad (5)", devices[2].DisplayName);
}

static void DeckLinkPersistentIdentityIsResolved()
{
    // Synthetic fixture values only; never copy identifiers from production hardware.
    var sinks = new[]
    {
        new DeckLinkSink("80:a1b2c3d0:00000000", "DeckLink Quad (1)"),
        new DeckLinkSink("80:a1b2c3d1:00000000", "DeckLink Quad (2)")
    };
    var hardware = new[]
    {
        new DeckLinkHardwareIdentity(sinks[0].FfmpegAddress, sinks[0].DisplayName, "DeckLink Quad 2", 0xa1b2c3d0, 0xa1b2c3d0, 0x00502000, 0, true, false),
        new DeckLinkHardwareIdentity(sinks[1].FfmpegAddress, sinks[1].DisplayName, "DeckLink Quad 2", 0xa1b2c3d1, 0xa1b2c3d0, 0x00502002, 1)
    };

    var ports = DeckLinkIdentityResolver.Resolve(sinks, hardware);
    Equal("DECKLINK-PERSISTENT-A1B2C3D0", ports[0].StableId);
    Equal("DECKLINK-PERSISTENT-A1B2C3D1", ports[1].StableId);
    Equal("0xA1B2C3D0", ports[0].DeviceGroupId);
    Equal(1, ports[1].SubdeviceIndex);
    Equal(true, ports[0].HasReferenceInput!.Value);
    Equal(false, ports[0].ReferenceSignalLocked!.Value);
    Equal(DeckLinkIdentityResolver.LegacyStableId(sinks[0].FfmpegAddress), ports[0].PreviousStableIds!.Single());
    True(ports.All(port => port.IdentityConfidence.Contains("stable across PCIe slots", StringComparison.Ordinal)));
    var reordered = DeckLinkIdentityResolver.Resolve(sinks.Reverse().ToArray(), hardware.Reverse().ToArray());
    foreach (var port in ports)
        Equal(port.StableId, reordered.Single(candidate => candidate.FfmpegName == port.FfmpegName).StableId);
    var deferred = DeckLinkIdentityMigration.DeferUntilRestart(ports);
    Equal(ports[0].PreviousStableIds!.Single(), deferred[0].StableId);
    True(deferred[0].IdentityConfidence.Contains("deferred until restart", StringComparison.Ordinal));

    var duplicateHardware = new[]
    {
        hardware[0],
        hardware[1] with { PersistentId = hardware[0].PersistentId }
    };
    var fallback = DeckLinkIdentityResolver.Resolve(sinks, duplicateHardware);
    True(fallback.All(port => port.StableId.StartsWith("FFMPEG-NAME-", StringComparison.Ordinal)));
}

static void DeckLinkHumanIdentityLabelsAreStable()
{
    var ports = new[]
    {
        new DeckLinkPort("DECKLINK-PERSISTENT-101", "sdk:101", "DeckLink Quad 2", 1, 0, null, [],
            FriendlyName: "Input 1", PersistentId: "0x101", DeviceGroupId: "0x100", CardFriendlyName: "Studio input card"),
        new DeckLinkPort("DECKLINK-PERSISTENT-102", "sdk:102", "DeckLink Quad 2", 1, 1, null, [],
            FriendlyName: "Input 2", PersistentId: "0x102", DeviceGroupId: "0x100", CardFriendlyName: "Studio input card")
    };

    Equal("Studio input card / Input 1", DeckLinkDisplayName.Full(ports[0]));
    Equal("…TENT-101", DeckLinkDisplayName.ShortIdentity(ports[0].StableId));
    var moved = ports[0] with { CardIndex = 0, PciLocation = "PCI:02:00.0" };
    Equal(DeckLinkDisplayName.Full(ports[0]), DeckLinkDisplayName.Full(moved));
    Equal("DeckLink card 3", DeckLinkDisplayName.Card(ports[0] with { CardIndex = 2, CardFriendlyName = null }));
}

static void DeckLinkVisualAssetCatalogMatchesSafely()
{
    var directory = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-decklink-assets-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(directory, "decklink-quad-2"));
    try
    {
        var catalog = new DeckLinkAssetCatalog(directory);
        True(!catalog.Status.Installed);
        var productPath = Path.Combine(directory, "decklink-quad-2", "product.jpg");
        File.WriteAllText(productPath, "synthetic image fixture");
        File.WriteAllText(Path.Combine(directory, "decklink-quad-2", "accessories.svg"), "<svg onload='alert(1)'></svg>");
        File.WriteAllText(Path.Combine(directory, "manifest.min.json"), """
            {
              "models": [
                {
                  "name": "DeckLink Quad 2",
                  "slug": "decklink-quad-2",
                  "category": "Multi-channel 3G-SDI",
                  "ports": "8 configurable SDI connectors",
                  "assets": {
                    "product": { "path": "decklink-quad-2/product.jpg", "width": 662, "height": 323 },
                    "physical": { "path": "../outside.jpg", "width": 10, "height": 10 },
                    "accessories": { "path": "decklink-quad-2/accessories.svg", "width": 10, "height": 10 }
                  }
                }
              ]
            }
            """);
        True(catalog.Status.Installed);
        Equal(1, catalog.Status.ModelCount);
        var match = catalog.Match("Blackmagic Design DeckLink Quad 2 (7)")!;
        Equal("DeckLink Quad 2", match.ModelName);
        Equal("Multi-channel 3G-SDI", match.Category);
        True(match.HasAsset("product"));
        True(!match.HasAsset("physical"));
        True(!match.HasAsset("accessories"));
        True(catalog.TryGetAsset(match.Slug, "product", out var asset));
        Equal(Path.GetFullPath(productPath), asset!.FullPath);
        Equal("image/jpeg", asset.ContentType);
        True(!catalog.TryGetAsset(match.Slug, "physical", out _));
        True(!catalog.TryGetAsset("../decklink-quad-2", "product", out _));
        Equal("DeckLink Quad 2", catalog.Match("DeckLink Quad 2 (simulation)")!.ModelName);
        True(catalog.Match("DeckLink model not in manifest") is null);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void DeckLinkIdentityReferencesAreMigrated()
{
    // Synthetic fixture values and labels only; never copy production topology into tests.
    var oldId = DeckLinkIdentityResolver.LegacyStableId("80:a1b2c3d0:00000000");
    var newId = "DECKLINK-PERSISTENT-A1B2C3D0";
    var port = new DeckLinkPort(newId, "80:a1b2c3d0:00000000", "DeckLink Quad 2", 0, 0, null, [],
        FriendlyName: "Studio Output A", PersistentId: "0xA1B2C3D0", PreviousStableIds: [oldId]);
    var aliases = DeckLinkIdentityMigration.BuildAliasMap([port]);
    var settings = new OperatorSettings
    {
        DeckLinkPortOverrides =
        [
            new() { StableId = oldId, FriendlyName = "Studio Output A", PortGroup = "TX", Reserved = true },
            new() { StableId = "DECKLINK-PERSISTENT-A1B2C3D1", FriendlyName = "DeckLink Quad (2)] (none)" }
        ],
        ManualSources = [new() { FixedPortId = oldId }],
        Rules = [new() { FixedPortId = oldId }]
    };
    var secondPort = port with { StableId = "DECKLINK-PERSISTENT-A1B2C3D1", FriendlyName = "DeckLink Quad (2)", PreviousStableIds = [] };
    True(DeckLinkIdentityMigration.MigrateSettings(settings, aliases, [port, secondPort]));
    Equal(newId, settings.DeckLinkPortOverrides[0].StableId);
    Equal("Studio Output A", settings.DeckLinkPortOverrides[0].FriendlyName);
    Equal("DeckLink Quad (2)", settings.DeckLinkPortOverrides[1].FriendlyName);
    Equal(newId, settings.ManualSources.Single().FixedPortId);
    Equal(newId, settings.Rules.Single().FixedPortId);

    var now = DateTimeOffset.UtcNow;
    var route = new RuntimeRoute("A/live/_definst_/one", "One", oldId, "Studio Output A", "1080i50", RouteState.Running,
        AssignmentMode.Manual, false, 100, 0, 100, 25, 1, 0, 0, now, now, null, null);
    var migrated = DeckLinkIdentityMigration.MigrateRoute(route, aliases,
        new Dictionary<string, DeckLinkPort>(StringComparer.OrdinalIgnoreCase) { [newId] = port });
    Equal(newId, migrated.PortId);
    Equal("DeckLink card 1 / Studio Output A", migrated.PortName);
}

static void DeckLinkMigrationDefersOnlyLegacyReferences()
{
    var oldId = DeckLinkIdentityResolver.LegacyStableId("80:synthetic:00000000");
    var newId = "DECKLINK-PERSISTENT-SYNTHETIC";
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldId] = newId };
    var settings = new OperatorSettings
    {
        DeckLinkPortOverrides = [new() { StableId = newId, FriendlyName = "Output A", IsOutputPort = true }]
    };
    True(!DeckLinkIdentityMigration.HasLegacyReferences(settings, [], aliases));
    settings.DeckLinkPortOverrides[0].StableId = oldId;
    True(DeckLinkIdentityMigration.HasLegacyReferences(settings, [], aliases));
    settings.DeckLinkPortOverrides[0].StableId = newId;
    var now = DateTimeOffset.UtcNow;
    var legacyRoute = new RuntimeRoute("S/live/_definst_/one", "One", oldId, "Output A", "1080p25",
        RouteState.Running, AssignmentMode.Manual, false, 0, 0, 1, 25, 1, 0, 0, now, now, null, null);
    True(DeckLinkIdentityMigration.HasLegacyReferences(settings, [legacyRoute], aliases));
}

static void WowzaInstanceEndpointIsCorrect()
{
    var server = Server("rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}");
    var provider = new WowzaDiscoveryProvider(new HttpClient(), server, new StaticCredentialResolver(new CredentialValue("user", "secret")));
    Equal("http://127.0.0.1:8087/v2/servers/_defaultServer_/vhosts/_defaultVHost_/applications/live/instances/_definst_",
        provider.BuildInstanceEndpoint("live", "_definst_").AbsoluteUri.TrimEnd('/'));
}

static void CommonPresetsAreAvailable()
{
    var presets = OutputPresetProfile.CommonDefaults();
    Equal(4, presets.Count);
    True(presets.Any(x => x.Id == "1080p25"));
    True(presets.Any(x => x.Id == "1080p50"));
    True(presets.Any(x => x.Id == "1080i50" && x.Interlaced));
    True(presets.Any(x => x.Id == "720p50" && x.Width == 1280));
}

static void OutputScanSelectionRoundTrips()
{
    Equal(OutputScanSelection.Progressive, OutputScanSelection.Format(false));
    Equal(OutputScanSelection.Interlaced, OutputScanSelection.Format(true));
    True(OutputScanSelection.TryParse(OutputScanSelection.Progressive, out var progressive) && !progressive);
    True(OutputScanSelection.TryParse(OutputScanSelection.Interlaced, out var interlaced) && interlaced);
    True(!OutputScanSelection.TryParse("", out _));
    True(!OutputScanSelection.TryParse("True", out _));
    var mislabeled = new OutputPresetProfile { Id = "1080i50", Name = "1080i50", Interlaced = false };
    Equal("1920x1080 25p", OutputScanSelection.Describe(mislabeled));
    True(OutputScanSelection.StandardPresetMismatch(mislabeled) is not null);
    True(OutputScanSelection.TryApplyStandardPreset(mislabeled));
    Equal("1920x1080 50i", OutputScanSelection.Describe(mislabeled));
    True(OutputScanSelection.StandardPresetMismatch(mislabeled) is null);
}

static void ManualRoutePresetSelectionIsValidated()
{
    var presets = OutputPresetProfile.CommonDefaults();
    Equal("1080i50", OutputPresetSelection.Resolve(presets, "1080p25", " 1080I50 ").Id);
    Equal("1080p50", OutputPresetSelection.Resolve(presets, "1080p50", null).Id);
    Equal("1080p25", OutputPresetSelection.Resolve(presets, "missing-rule-preset", null).Id);
    Throws<InvalidOperationException>(() => OutputPresetSelection.Resolve(presets, "1080p25", "deleted-preset"));
    Throws<InvalidOperationException>(() => OutputPresetSelection.Resolve([], "1080p25", null));
    OutputPresetSelection.EnsureReferencesAvailable(presets, ["1080p25", "1080I50"]);
    Throws<InvalidOperationException>(() => OutputPresetSelection.EnsureReferencesAvailable(presets, ["deleted-preset"]));
}

static void SourceRouteActionReflectsCurrentRoute()
{
    Equal(SourceRouteActionKind.Start, SourceRouteActionPolicy.Resolve(null));
    var now = DateTimeOffset.UtcNow;
    var route = new RuntimeRoute("A/live/_definst_/one", "One", "PORT-1", "Output 1", "1080p25",
        RouteState.Running, AssignmentMode.Manual, false, 0, 0, 1, 25, 1, 0, 0, now, now, null, null);
    Equal(SourceRouteActionKind.View, SourceRouteActionPolicy.Resolve(route));
    Equal(SourceRouteActionKind.View, SourceRouteActionPolicy.Resolve(route with { State = RouteState.WaitingForPort }));
    Equal(SourceRouteActionKind.View, SourceRouteActionPolicy.Resolve(route with { State = RouteState.WaitingForStream }));
    Equal(SourceRouteActionKind.Retry, SourceRouteActionPolicy.Resolve(route with { State = RouteState.Failed }));
    Equal(SourceRouteActionKind.Start, SourceRouteActionPolicy.Resolve(route with { State = RouteState.Released }));
}

static void SourceRefreshIsBackendConfirmed()
{
    var page = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "BroadcastRouter.Web", "Components", "Pages", "Sources.razor"));
    var coordinator = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "BroadcastRouter.Web", "Services", "RouterCoordinator.cs"));
    var pageText = File.ReadAllText(page);
    var coordinatorText = File.ReadAllText(coordinator);
    True(pageText.Contains("Commands.ExecuteAsync(\"refresh-sources\")", StringComparison.Ordinal));
    True(pageText.Contains("Source discovery completed and confirmed by the server", StringComparison.Ordinal));
    True(!pageText.Contains("Commands.ExecuteAsync(\"rescan\")", StringComparison.Ordinal));
    True(coordinatorText.Contains("Immediate source discovery requested.", StringComparison.Ordinal));
    True(coordinatorText.Contains("DiscoverAndProbeAsync(cancellationToken, force: true)", StringComparison.Ordinal));
}

static void RoutingRuleWildcardMatches()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    var rules = new[]
    {
        new RoutingRuleProfile { Id = "sports", Order = 10, StreamPattern = "sports-*", PresetId = "720p50" },
        new RoutingRuleProfile { Id = "all", Order = 20, ServerPattern = "SIM-*", StreamPattern = "*.stream", PresetId = "1080p50", PriorityAdjustment = 7, LockAssignment = true }
    };
    var decision = RoutingRuleEvaluator.Evaluate(source, rules, "1080p25");
    Equal("1080p50", decision.PresetId);
    Equal("all", decision.RuleId);
    Equal(107, decision.Priority);
    True(decision.Locked);
}

static void InvalidRoutingRegexIsRejected() => Throws<ArgumentException>(() => RoutingRuleEvaluator.ValidatePattern("regex:[unterminated"));

static void FallbackCommandIsSafeAndUncompressed()
{
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = OutputPresetProfile.CommonDefaults()[0].ToDomain();
    var start = FfmpegCommandBuilder.BuildFallback(new FfmpegRouteOptions("ffmpeg.exe"), port, preset, FallbackMode.TestPattern, null);
    True(!start.UseShellExecute);
    True(start.ArgumentList.Contains("smptebars=size=1920x1080:rate=25/1"));
    True(!start.ArgumentList.Contains("-b:v"));
    Equal(port.FfmpegName, start.ArgumentList[^1]);
    var arguments = start.ArgumentList.ToList();
    var audioInput = arguments.IndexOf("anullsrc=r=48000:cl=stereo");
    var firstMap = arguments.IndexOf("-map");
    True(audioInput >= 0 && audioInput < firstMap);

    var interlaced = OutputPresetProfile.CommonDefaults().Single(item => item.Id == "1080i50").ToDomain();
    var interlacedStart = FfmpegCommandBuilder.BuildFallback(new FfmpegRouteOptions("ffmpeg.exe"), port, interlaced, FallbackMode.Black, null);
    True(interlacedStart.ArgumentList.Contains("color=c=black:size=1920x1080:rate=50/1"));
    True(interlacedStart.ArgumentList.Any(argument => argument.Contains("tinterlace=interleave_top", StringComparison.Ordinal)));
}

static void PortStandbyCommandIsBroadcastSafe()
{
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0] with
    {
        CardFriendlyName = "Transmission card",
        FriendlyName = "Output 1"
    };
    var logoPath = Path.GetTempFileName();
    try
    {
        var preset = OutputPresetProfile.CommonDefaults()[0].ToDomain();
        var start = FfmpegCommandBuilder.BuildPortStandby(new FfmpegRouteOptions("ffmpeg.exe"), port, preset,
            new PortStandbyConfiguration(StandbyPattern.SmpteBars, logoPath, "Stream-3", true));
        True(start.ArgumentList.Contains("smptebars=size=1920x1080:rate=25/1"));
        True(start.ArgumentList.Contains("anullsrc=r=48000:cl=stereo"));
        True(start.ArgumentList.Contains("pcm_s16le"));
        var audioFilter = start.ArgumentList[start.ArgumentList.IndexOf("-af") + 1];
        True(audioFilter.Contains("volume=0", StringComparison.Ordinal));
        True(audioFilter.Contains("asetpts=N/SR/TB", StringComparison.Ordinal));
        True(start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden);
        var graph = start.ArgumentList[start.ArgumentList.IndexOf("-filter_complex") + 1];
        True(graph.Contains("Transmission card  -  SDI 1", StringComparison.Ordinal));
        True(graph.Contains("Stream-3", StringComparison.Ordinal));
        True(graph.Contains("fontfile='C\\:/Windows/Fonts/arial.ttf'", StringComparison.Ordinal));
        True(graph.Contains("%{localtime\\:%H\\\\\\:%M\\\\\\:%S}", StringComparison.Ordinal));
        True(graph.Contains("%{localtime\\:%A %d %B %Y}", StringComparison.Ordinal));
        True(graph.Contains("split=4[logo_tl][logo_tr][logo_bl][logo_br]", StringComparison.Ordinal));
        Equal(4, graph.Split("overlay=", StringSplitOptions.None).Length - 1);
        True(graph.Contains("x=(w-tw)/2", StringComparison.Ordinal));
        True(graph.Contains("y=h-th-", StringComparison.Ordinal));
        True(!start.ArgumentList.Contains("-b:v"));
        Equal(port.FfmpegName, start.ArgumentList[^1]);
    }
    finally
    {
        File.Delete(logoPath);
    }
}

static void WindowsServiceInstallerIsAutomaticAndRecoverable()
{
    var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "scripts", "Install-WindowsService.ps1"));
    var text = File.ReadAllText(script);
    True(text.Contains("[PSCredential] $Credential", StringComparison.Ordinal));
    True(text.Contains("StartupType = 'Automatic'", StringComparison.Ordinal));
    True(text.Contains("failure $ServiceName", StringComparison.Ordinal));
    True(text.Contains("failureflag $ServiceName 1", StringComparison.Ordinal));
    True(text.Contains("SeServiceLogonRight", StringComparison.Ordinal));
    True(text.Contains("Broadcast Router $([char]0x2013) Version $version", StringComparison.Ordinal));
}

static void SettingsRejectInvalidGuiValues()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings();
        settings.ManualSources.Add(new ManualSourceProfile { FriendlyName = "Invalid", RtspUrl = "http://not-rtsp.example/stream" });
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.ManualSources.Clear();
        settings.Security.Port = 70000;
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.Security.Port = 5080;
        settings.Presets[0].StandbyMode = FallbackMode.StandbySource;
        settings.Presets[0].StandbyValue = "not-an-rtsp-url";
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.Presets[0].StandbyMode = FallbackMode.Black;
        settings.Presets[0].StandbyValue = "";
        settings.Presets[0].Id = "1080i50";
        settings.Presets[0].Name = "1080i50";
        settings.Presets[0].Interlaced = false;
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.Presets[0].Interlaced = true;
        settings.ManualSources.Add(new ManualSourceProfile { FriendlyName = "Wrong connector", RtspUrl = "rtsp://127.0.0.1/live/test", FixedPortId = "INPUT-1" });
        settings.DeckLinkPortOverrides.Add(new DeckLinkPortOverride { StableId = "INPUT-1", FriendlyName = "Input 1", IsOutputPort = false });
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.ManualSources.Clear();
        settings.DeckLinkPortOverrides[0].IsOutputPort = true;
        settings.DeckLinkPortOverrides[0].StandbyPresetId = settings.Presets[0].Id;
        settings.DeckLinkPortOverrides[0].StandbyLabel = "unsafe:label";
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.DeckLinkPortOverrides[0].StandbyLabel = "Safe label";
        settings.Routing.InputReadTimeoutMilliseconds = 499;
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
    });
}

static void SettingsRejectAmbiguousDeckLinkCardNames()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings
        {
            DeckLinkCardOverrides =
            [
                new() { DeviceGroupId = "CARD-A", FriendlyName = "Studio card" },
                new() { DeviceGroupId = "CARD-B", FriendlyName = "studio CARD" }
            ]
        };
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
    });
}

static void SettingsRejectInvalidCidrRanges()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings();
        settings.Security.AllowedNetworks = "10.0.0.0/33";
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        settings.Security.AllowedNetworks = "not-an-address";
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
        True(NetworkAccessPolicy.IsAllowed(System.Net.IPAddress.Parse("::ffff:10.1.2.3"), "10.1.2.0/24"));
        True(!NetworkAccessPolicy.IsAllowed(System.Net.IPAddress.Parse("10.1.3.3"), "10.1.2.0/24"));
    });
}

static void NetworkExposureIsFailClosed()
{
    NetworkAccessPolicy.ValidateExposure("127.0.0.1", false);
    Throws<InvalidOperationException>(() => NetworkAccessPolicy.ValidateExposure("0.0.0.0", false));
    NetworkAccessPolicy.ValidateExposure("0.0.0.0", true);
    Equal(1, NetworkAccessPolicy.ParseTrustedProxies("10.0.0.10").Count);
    Throws<InvalidOperationException>(() => NetworkAccessPolicy.ParseTrustedProxies("10.0.0.0/24"));
    True(NetworkAccessPolicy.IsClientAllowed(System.Net.IPAddress.Loopback, System.Net.IPAddress.Loopback, "127.0.0.1/32"));
    True(!NetworkAccessPolicy.IsClientAllowed(System.Net.IPAddress.Parse("10.0.0.10"), System.Net.IPAddress.Loopback, "127.0.0.1/32"));
    True(NetworkAccessPolicy.IsClientAllowed(System.Net.IPAddress.Parse("10.0.0.10"), System.Net.IPAddress.Parse("192.168.5.2"), "192.168.5.0/24"));
}

static void SettingsRejectEmbeddedCredentials()
{
    Throws<FormatException>(() => RtspUrlGenerator.ValidateTemplate("rtsp://operator:secret@{wowza-host}/{stream-name}"));
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings();
        settings.ManualSources.Add(new ManualSourceProfile { FriendlyName = "Credentialed", RtspUrl = "rtsp://operator:secret@example.test/live/feed" });
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(settings).GetAwaiter().GetResult());
    });
}

static void FailedValidationPreservesActiveSettings()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var valid = new OperatorSettings { Security = new() { Port = 5099 } };
        store.SaveSettingsAsync(valid).GetAwaiter().GetResult();
        var invalid = new OperatorSettings { Security = new() { Port = 70000 } };
        Throws<InvalidOperationException>(() => store.SaveSettingsAsync(invalid).GetAwaiter().GetResult());
        Equal(5099, store.LoadSettingsAsync().GetAwaiter().GetResult().Security.Port);
    });
}

static void AllGuiSettingsRoundTrip()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings
        {
            SimulationMode = true,
            MediaTools = new() { FfmpegPath = @"C:\Media\ffmpeg.exe", FfprobePath = @"C:\Media\ffprobe.exe", FfplayPath = @"C:\Media\ffplay.exe" },
            Routing = new() { AutomaticRoutingEnabled = false, InputReadTimeoutMilliseconds = 1250, ReservationGraceSeconds = 31, StableRestoreSeconds = 6, FirstProgressTimeoutSeconds = 22, StallTimeoutSeconds = 11, FrozenInputTimeoutSeconds = 13, GracefulStopSeconds = 7, MaxRetryAttempts = 9, RetryDelaysSeconds = [2, 4, 8] },
            Security = new() { BindAddress = "127.0.0.1", Port = 5085, RequireAuthentication = true, HttpsEnabled = true, AllowedNetworks = "127.0.0.1/32", TrustedProxies = "127.0.0.2", SessionTimeoutMinutes = 60 }
        };
        settings.WowzaServers.Add(new WowzaServerProfile
        {
            FriendlyName = "QA Wowza",
            ServerId = " WOWZA-QA ",
            ManagementUrl = "http://10.0.0.1:8087/",
            Username = "operator",
            ProtectedPassword = "protected",
            ValidateTlsCertificate = false,
            RtspHost = "10.0.0.1",
            RtspPort = 8698,
            Applications = "live,news",
            ApplicationInstances = "_definst_",
            PollingIntervalSeconds = 7,
            ConnectionTimeoutSeconds = 9,
            RtspUrlTemplate = "rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}",
            Enabled = false,
            Priority = 123
        });
        settings.ManualSources.Add(new ManualSourceProfile { StableId = "manual-qa", FriendlyName = "QA manual", RtspUrl = "rtsp://10.0.0.2/live/test", Priority = 77, FixedPortId = "PORT-2", Locked = true, Enabled = false });
        settings.DeckLinkCardOverrides.Add(new DeckLinkCardOverride { DeviceGroupId = "CARD-PERSISTENT-2", FriendlyName = "Studio input card" });
        settings.DeckLinkPortOverrides.Add(new DeckLinkPortOverride
        {
            StableId = "PORT-2",
            FriendlyName = "Transmission 2",
            PortGroup = "TX",
            Reserved = true,
            IsOutputPort = true,
            StandbyEnabled = true,
            StandbyPresetId = "qa-1080p25",
            StandbyPattern = StandbyPattern.SmpteHdBars,
            StandbyLabel = "TX 2",
            StandbyShowClock = true
        });
        settings.Presets = [new OutputPresetProfile { Id = "qa-1080p25", Name = "QA 1080p25", Width = 1920, Height = 1080, FrameRateNumerator = 25, FrameRateDenominator = 1, Interlaced = false, PixelFormat = "uyvy422", IncludeAudio = true, LowLatency = false, BufferSizeMegabytes = 512, StandbyMode = FallbackMode.TestPattern, StandbyValue = "bars" }];
        settings.Rules.Add(new RoutingRuleProfile { Id = "rule-qa", Name = "QA rule", Order = 20, Enabled = false, ServerPattern = "WOWZA-*", ApplicationPattern = "live", InstancePattern = "*", StreamPattern = "qa*", Codec = "h264", Tag = "news", PresetId = "qa-1080p25", FixedPortId = "PORT-2", LockAssignment = true });

        store.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        var loaded = store.LoadSettingsAsync().GetAwaiter().GetResult();
        Equal(6, loaded.SchemaVersion);
        True(loaded.SimulationMode && !loaded.Routing.AutomaticRoutingEnabled);
        Equal(@"C:\Media\ffplay.exe", loaded.MediaTools.FfplayPath);
        Equal("WOWZA-QA", loaded.WowzaServers.Single().ServerId);
        Equal(9, loaded.WowzaServers.Single().ConnectionTimeoutSeconds);
        Equal(8698, loaded.WowzaServers.Single().RtspPort);
        Equal("manual-qa", loaded.ManualSources.Single().StableId);
        Equal("PORT-2", loaded.ManualSources.Single().FixedPortId);
        Equal("Studio input card", loaded.DeckLinkCardOverrides.Single().FriendlyName);
        Equal("Transmission 2", loaded.DeckLinkPortOverrides.Single().FriendlyName);
        True(loaded.DeckLinkPortOverrides.Single().IsOutputPort);
        Equal(StandbyPattern.SmpteHdBars, loaded.DeckLinkPortOverrides.Single().StandbyPattern);
        Equal("TX 2", loaded.DeckLinkPortOverrides.Single().StandbyLabel);
        Equal("qa-1080p25", loaded.Presets.Single().Id);
        Equal(512, loaded.Presets.Single().BufferSizeMegabytes);
        Equal("rule-qa", loaded.Rules.Single().Id);
        Equal(5085, loaded.Security.Port);
        Equal("127.0.0.2", loaded.Security.TrustedProxies);
        Equal(22, loaded.Routing.FirstProgressTimeoutSeconds);
        Equal(13, loaded.Routing.FrozenInputTimeoutSeconds);
        Equal(1250, loaded.Routing.InputReadTimeoutMilliseconds);
        Equal(9, loaded.Routing.MaxRetryAttempts);
        Equal(3, loaded.Routing.RetryDelaysSeconds.Length);

        loaded.WowzaServers.Clear();
        loaded.ManualSources.Clear();
        loaded.DeckLinkCardOverrides.Clear();
        loaded.DeckLinkPortOverrides.Clear();
        loaded.Rules.Clear();
        store.SaveSettingsAsync(loaded).GetAwaiter().GetResult();
        var deleted = store.LoadSettingsAsync().GetAwaiter().GetResult();
        Equal(0, deleted.WowzaServers.Count);
        Equal(0, deleted.ManualSources.Count);
        Equal(0, deleted.DeckLinkCardOverrides.Count);
        Equal(0, deleted.DeckLinkPortOverrides.Count);
        Equal(0, deleted.Rules.Count);
    });
}

static void SqliteSettingsPersist()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var settings = new OperatorSettings { SimulationMode = false, MediaTools = new() { FfmpegPath = @"C:\tools\ffmpeg.exe", FfprobePath = @"C:\tools\ffprobe.exe" } };
        store.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        var loaded = store.LoadSettingsAsync().GetAwaiter().GetResult();
        True(!loaded.SimulationMode);
        Equal(@"C:\tools\ffmpeg.exe", loaded.MediaTools.FfmpegPath);
    });
}

static void StaleSettingsRevisionIsRejected()
{
    var settings = new OperatorSettings { ConfigurationRevision = 4 };
    SettingsConcurrencyPolicy.EnsureCurrent(settings.ConfigurationRevision, 4);
    Throws<InvalidOperationException>(() => SettingsConcurrencyPolicy.EnsureCurrent(settings.ConfigurationRevision, 5));
    var appliedAt = DateTimeOffset.UtcNow;
    SettingsConcurrencyPolicy.MarkApplied(settings, 4, appliedAt, "admin");
    Equal(5L, settings.ConfigurationRevision);
    Equal(appliedAt, settings.LastAppliedAt!.Value);
    Equal("admin", settings.LastAppliedBy);
}

static void SqliteRoutesRestore()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var now = DateTimeOffset.UtcNow;
        var route = new RuntimeRoute("A/live/_definst_/one", "One", "PORT-1", "Output 1", "1080p25", RouteState.Running,
            AssignmentMode.Fixed, true, 100, 2, 500, 25, 1, 3, 4, now.AddMinutes(-1), now, null, null);
        store.SaveRouteAsync(route, RouteState.Starting).GetAwaiter().GetResult();
        var restored = store.LoadRoutesAsync().GetAwaiter().GetResult().Single();
        Equal("PORT-1", restored.PortId);
        Equal(RouteState.Running, restored.State);
        True(restored.Locked);
        Equal(2, restored.RestartCount);
    });
}

static void SqliteSourcesRestore()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var source = new DiscoveredSource(new SourceIdentity("QA", "live", "_definst_", "offline.stream"),
            "Offline feed", new Uri("rtsp://127.0.0.1:8698/live/offline.stream"),
            SourceState.PublisherDisconnected, 42, Tags: new HashSet<string> { "offline", "qa" },
            LastObservedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        store.UpsertSourceAsync(source).GetAwaiter().GetResult();
        var restored = store.LoadSourcesAsync().GetAwaiter().GetResult().Single();
        Equal(source.Identity.Value, restored.Identity.Value);
        Equal(SourceState.PublisherDisconnected, restored.State);
        Equal("Offline feed", restored.FriendlyName);
        True(restored.Tags!.SetEquals(["offline", "qa"]));
    });
}

static void SqliteLogsAreRedacted()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        store.WriteLogAsync("Error", "RTSP", "Failed rtsp://operator:secret@127.0.0.1/live/input", "source-1").GetAwaiter().GetResult();
        var entry = store.ReadLogsAsync().GetAwaiter().GetResult().Single();
        True(!entry.Message.Contains("secret", StringComparison.Ordinal));
        True(!entry.Message.Contains("127.0.0.1", StringComparison.Ordinal));
        True(entry.Message.Contains("***", StringComparison.Ordinal));
    });
}

static void SqliteConfigurationAuditPersists()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var timestamp = DateTimeOffset.UtcNow;
        store.WriteConfigurationAuditAsync(new(0, timestamp, "OutputPortConfiguration", "PORT-SYNTHETIC",
            "Transmission card", "Output 1", "output=False", "output=True", "admin", "Operator settings save",
            "Persisted and applied", "SOURCE-SYNTHETIC")).GetAwaiter().GetResult();
        var entry = store.ReadConfigurationAuditAsync().GetAwaiter().GetResult().Single();
        Equal("OutputPortConfiguration", entry.EventType);
        Equal("output=False", entry.PreviousState);
        Equal("output=True", entry.NewState);
        Equal("admin", entry.Actor);
        Equal("SOURCE-SYNTHETIC", entry.SourceId);
    });
}

static void SqliteIntegrityIsOk()
{
    WithSqliteStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        Equal("ok", store.IntegrityCheckAsync().GetAwaiter().GetResult());
    });
}

static void DefaultConfigurationIsProductionSafe()
{
    var settings = new OperatorSettings();
    True(!settings.SimulationMode);
    True(string.IsNullOrWhiteSpace(settings.MediaTools.FfmpegPath));
    True(settings.Security.BindAddress == "127.0.0.1");
    Equal(1000, settings.Routing.InputReadTimeoutMilliseconds);
}

static void WithSqliteStore(Action<SqliteDataStore> body)
{
    var directory = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-sqlite-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try { body(new SqliteDataStore(Path.Combine(directory, "test.db"))); }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

static WowzaServerConfiguration Server(string template) => new("Main", "MAIN", new Uri("http://127.0.0.1:8087/"), "cred-main", true, "127.0.0.1", 8698, ["live"], ["_definst_"], template, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

static void True(bool value) { if (!value) throw new Exception("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}', got '{actual}'."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }

sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
}
