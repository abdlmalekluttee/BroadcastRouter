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
    ("Startup failure releases reservation", StartupFailureReleasesReservation),
    ("Missing-source lease retention", MissingSourceLeaseRetentionHonorsLockAndGrace),
    ("Emergency stop blocks route starts", EmergencyStopBlocksRouteStarts),
    ("Locked route stop is refused before release", LockedRouteStopIsRefused),
    ("Administrator route commands are authorized", RouteCommandsRequireAdministrator),
    ("Priority waiting queue", HigherPriorityDequeuesFirst),
    ("Route transition validation", InvalidStateJumpIsRejected),
    ("Automatic assignment", AutomaticAssignmentUsesOnePort),
    ("Simulation discovery", SimulationReturnsUsableVideo),
    ("Retry policy caps delay", RetryPolicyCapsAtMaximum),
    ("Retry attempt cap is opt-in", RetryAttemptCapIsOptIn),
    ("FFmpeg command uses argument list", FfmpegCommandUsesArgumentList),
    ("FFmpeg display redacts credentials", FfmpegDisplayRedactsCredentials),
    ("Browser preview is tokenized with VU overlay", BrowserPreviewIsTokenizedWithVuOverlay),
    ("FFmpeg progress parsing", FfmpegProgressIsParsed),
    ("FFmpeg stall detection", FfmpegStallIsDetected),
    ("FFmpeg first-progress timeout", FfmpegFirstProgressTimeoutIsDetected),
    ("FFmpeg failure classification", FfmpegFailureIsClassified),
    ("FFprobe media parsing", FfprobeMediaIsParsed),
    ("FFprobe scan type parsing", FfprobeScanTypeIsParsed),
    ("FFprobe rejects metadata-only video", FfprobeRequiresReadFrames),
    ("FFprobe rejects malformed output", FfprobeRejectsMalformedOutput),
    ("Wowza incoming stream parsing", WowzaIncomingStreamsAreParsed),
    ("Renamed Wowza source is pruned", RenamedWowzaSourceIsPruned),
    ("Failed Wowza poll retains source", FailedWowzaPollRetainsSource),
    ("Log credential redaction", AuthenticatedUrisAreRedacted),
    ("Diagnostics omit sensitive data", DiagnosticsOmitSensitiveData),
    ("Windows DPAPI credential round trip", DpapiCredentialRoundTrips),
    ("Atomic operator settings persistence", OperatorSettingsPersistAtomically),
    ("DeckLink sink enumeration parsing", DeckLinkSinksAreParsed),
    ("Wowza instance discovery endpoint", WowzaInstanceEndpointIsCorrect),
    ("Common broadcast presets", CommonPresetsAreAvailable),
    ("Output scan selection round trip", OutputScanSelectionRoundTrips),
    ("Manual route preset selection", ManualRoutePresetSelectionIsValidated),
    ("Routing rule wildcard evaluation", RoutingRuleWildcardMatches),
    ("Routing regex validation", InvalidRoutingRegexIsRejected),
    ("Fallback command is uncompressed", FallbackCommandIsSafeAndUncompressed),
    ("SQLite settings persistence", SqliteSettingsPersist),
    ("All GUI settings round trip", AllGuiSettingsRoundTrip),
    ("Settings reject invalid GUI values", SettingsRejectInvalidGuiValues),
    ("Settings reject invalid CIDR ranges", SettingsRejectInvalidCidrRanges),
    ("Network exposure and proxy trust are fail-closed", NetworkExposureIsFailClosed),
    ("Settings reject embedded credentials", SettingsRejectEmbeddedCredentials),
    ("Failed validation preserves active settings", FailedValidationPreservesActiveSettings),
    ("SQLite route restart recovery", SqliteRoutesRestore),
    ("SQLite structured log redaction", SqliteLogsAreRedacted),
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

static void AutomaticAssignmentUsesOnePort()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result[0];
    var ports = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result;
    var manager = new PortReservationManager();
    var result = new AutomaticAssignmentEngine(manager, new PriorityWaitingQueue()).Assign(source, ports);
    True(result.Assigned);
    Equal(1, manager.Snapshot().Count);
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

static void FfmpegCommandUsesArgumentList()
{
    var source = new SimulationDiscoveryProvider().DiscoverAsync(default).Result.Single();
    var port = new SimulationDeckLinkEnumerator().EnumerateAsync(default).Result[0];
    var preset = new OutputPreset("HD25", "1080p25", new VideoMode(1920, 1080, 25, 1, "uyvy422"), true, 256);
    var start = FfmpegCommandBuilder.Build(new FfmpegRouteOptions("ffmpeg.exe"), source, port, preset);
    True(!start.UseShellExecute);
    True(start.ArgumentList.Contains("-progress"));
    True(start.ArgumentList.Contains(port.FfmpegName));
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

static void FfmpegFailureIsClassified()
{
    Equal(FfmpegFailureCategory.Authentication, FfmpegErrorClassifier.Classify(1, "RTSP server returned 401 Unauthorized"));
    Equal(FfmpegFailureCategory.DeckLinkBusy, FfmpegErrorClassifier.Classify(1, "Device or resource busy"));
    Equal(FfmpegFailureCategory.None, FfmpegErrorClassifier.Classify(0, ""));
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

static void FfprobeRequiresReadFrames()
{
    const string json = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"nb_read_frames":"N/A"}]}""";
    var result = FfprobeStreamProbe.Parse(json);
    True(result.Opened);
    True(!result.FramesReceived);
    Equal("NoVideoFrames", result.FailureCategory);
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
      80:80ce89d0:00000000 [DeckLink Quad (1)]
      80:80ce89d1:00000000 [DeckLink Quad (2)]
      80:80ce89d4:00000000 [DeckLink Quad (5)]
    """;
    var devices = FfmpegDiagnostics.ParseDeckLinkSinks(output);
    Equal(3, devices.Count);
    Equal("80:80ce89d0:00000000", devices[0].FfmpegAddress);
    Equal("DeckLink Quad (1)", devices[0].DisplayName);
    Equal("DeckLink Quad (5)", devices[2].DisplayName);
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
            Routing = new() { AutomaticRoutingEnabled = false, ReservationGraceSeconds = 31, StableRestoreSeconds = 6, FirstProgressTimeoutSeconds = 22, StallTimeoutSeconds = 11, GracefulStopSeconds = 7, MaxRetryAttempts = 9, RetryDelaysSeconds = [2, 4, 8] },
            Security = new() { BindAddress = "127.0.0.1", Port = 5085, RequireAuthentication = true, HttpsEnabled = true, AllowedNetworks = "127.0.0.1/32", TrustedProxies = "127.0.0.2", SessionTimeoutMinutes = 60 }
        };
        settings.WowzaServers.Add(new WowzaServerProfile
        {
            FriendlyName = "QA Wowza", ServerId = " WOWZA-QA ", ManagementUrl = "http://10.0.0.1:8087/", Username = "operator",
            ProtectedPassword = "protected", ValidateTlsCertificate = false, RtspHost = "10.0.0.1", RtspPort = 8698,
            Applications = "live,news", ApplicationInstances = "_definst_", PollingIntervalSeconds = 7, ConnectionTimeoutSeconds = 9,
            RtspUrlTemplate = "rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}", Enabled = false, Priority = 123
        });
        settings.ManualSources.Add(new ManualSourceProfile { StableId = "manual-qa", FriendlyName = "QA manual", RtspUrl = "rtsp://10.0.0.2/live/test", Priority = 77, FixedPortId = "PORT-2", Locked = true, Enabled = false });
        settings.DeckLinkPortOverrides.Add(new DeckLinkPortOverride { StableId = "PORT-2", FriendlyName = "Transmission 2", PortGroup = "TX", Reserved = true });
        settings.Presets = [new OutputPresetProfile { Id = "qa-1080p25", Name = "QA 1080p25", Width = 1920, Height = 1080, FrameRateNumerator = 25, FrameRateDenominator = 1, Interlaced = false, PixelFormat = "uyvy422", IncludeAudio = true, LowLatency = false, BufferSizeMegabytes = 512, StandbyMode = FallbackMode.TestPattern, StandbyValue = "bars" }];
        settings.Rules.Add(new RoutingRuleProfile { Id = "rule-qa", Name = "QA rule", Order = 20, Enabled = false, ServerPattern = "WOWZA-*", ApplicationPattern = "live", InstancePattern = "*", StreamPattern = "qa*", Codec = "h264", Tag = "news", PresetId = "qa-1080p25", FixedPortId = "PORT-2", LockAssignment = true });

        store.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        var loaded = store.LoadSettingsAsync().GetAwaiter().GetResult();
        Equal(3, loaded.SchemaVersion);
        True(loaded.SimulationMode && !loaded.Routing.AutomaticRoutingEnabled);
        Equal(@"C:\Media\ffplay.exe", loaded.MediaTools.FfplayPath);
        Equal("WOWZA-QA", loaded.WowzaServers.Single().ServerId);
        Equal(9, loaded.WowzaServers.Single().ConnectionTimeoutSeconds);
        Equal(8698, loaded.WowzaServers.Single().RtspPort);
        Equal("manual-qa", loaded.ManualSources.Single().StableId);
        Equal("PORT-2", loaded.ManualSources.Single().FixedPortId);
        Equal("Transmission 2", loaded.DeckLinkPortOverrides.Single().FriendlyName);
        Equal("qa-1080p25", loaded.Presets.Single().Id);
        Equal(512, loaded.Presets.Single().BufferSizeMegabytes);
        Equal("rule-qa", loaded.Rules.Single().Id);
        Equal(5085, loaded.Security.Port);
        Equal("127.0.0.2", loaded.Security.TrustedProxies);
        Equal(22, loaded.Routing.FirstProgressTimeoutSeconds);
        Equal(9, loaded.Routing.MaxRetryAttempts);
        Equal(3, loaded.Routing.RetryDelaysSeconds.Length);

        loaded.WowzaServers.Clear();
        loaded.ManualSources.Clear();
        loaded.DeckLinkPortOverrides.Clear();
        loaded.Rules.Clear();
        store.SaveSettingsAsync(loaded).GetAwaiter().GetResult();
        var deleted = store.LoadSettingsAsync().GetAwaiter().GetResult();
        Equal(0, deleted.WowzaServers.Count);
        Equal(0, deleted.ManualSources.Count);
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
