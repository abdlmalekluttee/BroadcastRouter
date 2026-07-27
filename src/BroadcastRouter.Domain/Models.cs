namespace BroadcastRouter.Domain;

public enum SourceState
{
    Known, Discovered, PublisherActive, Probing, Ready, WaitingForPort, Starting,
    Running, Reconnecting, PublisherDisconnected, RtspUnavailable, VideoStalled,
    AudioOnly, UnsupportedMedia, Failed, Disabled, Ignored
}

public enum RouteState
{
    Known, PublisherActive, Probing, Ready, WaitingForPort, Reserved, Starting,
    Running, Stalled, Reconnecting, Fallback, Unavailable, Released, Disabled, Failed
}

public enum AssignmentMode { None, Automatic, Rule, Fixed, Manual }
public enum FallbackMode { Black, TestPattern, FreezeLastFrame, StandbySource, File }

public sealed record WowzaServerConfiguration(
    string FriendlyName,
    string ServerId,
    Uri ManagementBaseUri,
    string CredentialReference,
    bool ValidateTlsCertificate,
    string RtspHost,
    int RtspPort,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> ApplicationInstances,
    string RtspUrlTemplate,
    TimeSpan PollingInterval,
    TimeSpan ConnectionTimeout,
    bool Enabled = true,
    int Priority = 0,
    IReadOnlySet<string>? Tags = null);

public sealed record MediaProperties(
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    double? FramesPerSecond,
    long? Bitrate,
    int? AudioSampleRate,
    int? AudioChannels,
    bool HasUsableVideo,
    bool? Interlaced = null);

public sealed record DiscoveredSource(
    SourceIdentity Identity,
    string FriendlyName,
    Uri RtspUri,
    SourceState State,
    int Priority,
    MediaProperties? Media = null,
    IReadOnlySet<string>? Tags = null,
    string? FixedPortId = null,
    bool AssignmentLocked = false,
    bool AutomaticRoutingEnabled = true,
    DateTimeOffset? LastObservedAt = null);

public sealed record VideoMode(int Width, int Height, int FrameRateNumerator, int FrameRateDenominator, string PixelFormat)
{
    public double FramesPerSecond => (double)FrameRateNumerator / FrameRateDenominator;
}

public sealed record DeckLinkPort(
    string StableId,
    string FfmpegName,
    string ModelName,
    int CardIndex,
    int SubdeviceIndex,
    string? PciLocation,
    IReadOnlyList<VideoMode> SupportedModes,
    bool IsAvailable = true,
    string? FriendlyName = null,
    string IdentityConfidence = "Unverified",
    bool Reserved = false,
    string PortGroup = "",
    string? PersistentId = null,
    string? DeviceGroupId = null,
    string? DeviceHandle = null,
    string? TopologicalId = null,
    IReadOnlyList<string>? PreviousStableIds = null,
    string? CardFriendlyName = null);

public static class DeckLinkDisplayName
{
    public static string Card(DeckLinkPort port) => string.IsNullOrWhiteSpace(port.CardFriendlyName)
        ? $"DeckLink card {port.CardIndex + 1}"
        : port.CardFriendlyName;

    public static string Connector(DeckLinkPort port) => string.IsNullOrWhiteSpace(port.FriendlyName)
        ? port.FfmpegName
        : port.FriendlyName;

    public static string Full(DeckLinkPort port) => $"{Card(port)} / {Connector(port)}";

    public static string ShortIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unavailable";
        var normalized = value.Trim();
        return normalized.Length <= 8 ? normalized : $"…{normalized[^8..]}";
    }
}

public sealed record OutputPreset(
    string Id,
    string Name,
    VideoMode Mode,
    bool LowLatency,
    int BufferSizeMegabytes,
    bool IncludeAudio = true,
    bool Interlaced = false);

public sealed record RouteRecord(
    SourceIdentity Source,
    string? PortId,
    string PresetId,
    RouteState State,
    AssignmentMode AssignmentMode,
    bool Locked,
    int RestartCount = 0,
    string? LastFailureCategory = null,
    string? LastFailureMessage = null);

public sealed record PortReservation(
    string PortId,
    SourceIdentity Source,
    bool Locked,
    DateTimeOffset ReservedAt);
