namespace BroadcastRouter.Domain;

public sealed class OperatorSettings
{
    public int SchemaVersion { get; set; } = 3;
    public bool SimulationMode { get; set; } = false;
    public MediaToolPaths MediaTools { get; set; } = new();
    public List<WowzaServerProfile> WowzaServers { get; set; } = [];
    public List<ManualSourceProfile> ManualSources { get; set; } = [];
    public List<OutputPresetProfile> Presets { get; set; } = OutputPresetProfile.CommonDefaults();
    public List<RoutingRuleProfile> Rules { get; set; } = [];
    public List<DeckLinkPortOverride> DeckLinkPortOverrides { get; set; } = [];
    public RoutingPolicySettings Routing { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
}

public sealed class DeckLinkPortOverride
{
    public string StableId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string PortGroup { get; set; } = "";
    public bool Reserved { get; set; }
}

public sealed class MediaToolPaths
{
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string FfplayPath { get; set; } = "";
}

public sealed class WowzaServerProfile
{
    public string FriendlyName { get; set; } = "Main Wowza";
    public string ServerId { get; set; } = "WOWZA-MAIN";
    public string ManagementUrl { get; set; } = "http://127.0.0.1:8087/";
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool ValidateTlsCertificate { get; set; } = true;
    public string RtspHost { get; set; } = "127.0.0.1";
    public int RtspPort { get; set; } = 1935;
    public string Applications { get; set; } = "live";
    public string ApplicationInstances { get; set; } = "_definst_";
    public string RtspUrlTemplate { get; set; } = "rtsp://{wowza-host}:{rtsp-port}/{application}/{stream-name}";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int ConnectionTimeoutSeconds { get; set; } = 8;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
}

public sealed class ManualSourceProfile
{
    public string StableId { get; set; } = Guid.NewGuid().ToString("N");
    public string FriendlyName { get; set; } = "Manual RTSP source";
    public string RtspUrl { get; set; } = "rtsp://127.0.0.1:1935/live/stream";
    public int Priority { get; set; } = 50;
    public string FixedPortId { get; set; } = "";
    public bool Locked { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class OutputPresetProfile
{
    public string Id { get; set; } = "1080p25";
    public string Name { get; set; } = "1080p25";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRateNumerator { get; set; } = 25;
    public int FrameRateDenominator { get; set; } = 1;
    public bool Interlaced { get; set; }
    public string PixelFormat { get; set; } = "uyvy422";
    public string AspectHandling { get; set; } = "Fit";
    public bool Deinterlace { get; set; }
    public bool IncludeAudio { get; set; } = true;
    public string RtspTransport { get; set; } = "tcp";
    public bool LowLatency { get; set; } = true;
    public int BufferSizeMegabytes { get; set; } = 256;
    public FallbackMode StandbyMode { get; set; } = FallbackMode.Black;
    public string StandbyValue { get; set; } = "";

    public OutputPreset ToDomain() => new(Id, Name,
        new VideoMode(Width, Height, FrameRateNumerator, Math.Max(1, FrameRateDenominator), PixelFormat),
        LowLatency, BufferSizeMegabytes, IncludeAudio, Interlaced);

    public static List<OutputPresetProfile> CommonDefaults() =>
    [
        new() { Id = "1080p25", Name = "1080p25", Width = 1920, Height = 1080, FrameRateNumerator = 25 },
        new() { Id = "1080p50", Name = "1080p50", Width = 1920, Height = 1080, FrameRateNumerator = 50 },
        new() { Id = "1080i50", Name = "1080i50", Width = 1920, Height = 1080, FrameRateNumerator = 25, Interlaced = true },
        new() { Id = "720p50", Name = "720p50", Width = 1280, Height = 720, FrameRateNumerator = 50 }
    ];
}

public sealed class RoutingRuleProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public int Order { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string ServerPattern { get; set; } = "*";
    public string ApplicationPattern { get; set; } = "*";
    public string InstancePattern { get; set; } = "*";
    public string StreamPattern { get; set; } = "*";
    public string Tag { get; set; } = "";
    public string Codec { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FramesPerSecond { get; set; }
    public bool? HasAudio { get; set; }
    public string PresetId { get; set; } = "1080p25";
    public string PortGroup { get; set; } = "";
    public string FixedPortId { get; set; } = "";
    public int PriorityAdjustment { get; set; }
    public bool LockAssignment { get; set; }
}

public sealed class RoutingPolicySettings
{
    public bool AutomaticRoutingEnabled { get; set; } = true;
    public int ReservationGraceSeconds { get; set; } = 30;
    public int StableRestoreSeconds { get; set; } = 5;
    public int StallTimeoutSeconds { get; set; } = 10;
    public int FirstProgressTimeoutSeconds { get; set; } = 20;
    public int GracefulStopSeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; }
    public int[] RetryDelaysSeconds { get; set; } = [1, 2, 5, 10, 20, 30];
}

public sealed class SecuritySettings
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5080;
    public bool RequireAuthentication { get; set; }
    public bool HttpsEnabled { get; set; }
    public string AllowedNetworks { get; set; } = "127.0.0.1/32;::1/128";
    public string TrustedProxies { get; set; } = "";
    public int SessionTimeoutMinutes { get; set; } = 30;
}
