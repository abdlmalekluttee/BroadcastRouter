using System.Security.Cryptography;
using System.Text;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public static class DiagnosticSanitizer
{
    public static object SanitizeSnapshot(RouterSnapshot snapshot) => new
    {
        snapshot.StartedAt,
        snapshot.UpdatedAt,
        snapshot.CpuPercent,
        snapshot.WorkingSetBytes,
        snapshot.SimulationMode,
        snapshot.EmergencyStopped,
        StatusMessage = LogRedactor.RedactForDiagnostics(snapshot.StatusMessage),
        SourceCounts = snapshot.Sources.GroupBy(source => source.State.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        Ports = snapshot.Ports.Select(port => new
        {
            Id = Opaque(port.StableId),
            port.IsAvailable,
            port.Reserved,
            port.IdentityConfidence,
            SupportedModeCount = port.SupportedModes.Count
        }),
        Routes = snapshot.Routes.Select(route => new
        {
            Source = Opaque(route.SourceId),
            Port = Opaque(route.PortId),
            Preset = Opaque(route.PresetId),
            route.State,
            route.AssignmentMode,
            route.Locked,
            route.Priority,
            route.RestartCount,
            route.Frame,
            route.Fps,
            route.Speed,
            route.DroppedFrames,
            route.DuplicatedFrames,
            route.StartedAt,
            route.UpdatedAt,
            route.FailureCategory,
            FailureMessage = LogRedactor.RedactForDiagnostics(route.FailureMessage ?? "")
        }),
        Waiting = snapshot.Waiting.Select(item => new { Source = Opaque(item.SourceId), item.Priority, Reason = LogRedactor.RedactForDiagnostics(item.Reason), item.Sequence }),
        Servers = snapshot.Servers.Select(server => new { server.Reachable, server.Authenticated, server.ActiveStreamCount, Summary = LogRedactor.RedactForDiagnostics(server.Summary), server.CheckedAt }),
        ToolValidation = new
        {
            snapshot.ToolValidation.State,
            snapshot.ToolValidation.DeckLinkCompiled,
            snapshot.ToolValidation.DesktopVideoInstalled,
            snapshot.ToolValidation.DeckLinkOutputCount,
            Findings = snapshot.ToolValidation.Findings.Select(LogRedactor.RedactForDiagnostics),
            snapshot.ToolValidation.CheckedAt
        }
    };

    public static object SanitizeSettings(OperatorSettings settings) => new
    {
        settings.SchemaVersion,
        settings.SimulationMode,
        WowzaServerCount = settings.WowzaServers.Count,
        EnabledWowzaServerCount = settings.WowzaServers.Count(server => server.Enabled),
        ManualSourceCount = settings.ManualSources.Count,
        DeckLinkOverrideCount = settings.DeckLinkPortOverrides.Count,
        Presets = settings.Presets.Select(preset => new
        {
            Id = Opaque(preset.Id),
            preset.Width,
            preset.Height,
            preset.FrameRateNumerator,
            preset.FrameRateDenominator,
            preset.Interlaced,
            preset.PixelFormat,
            preset.IncludeAudio,
            preset.LowLatency,
            preset.BufferSizeMegabytes,
            preset.StandbyMode
        }),
        RoutingRuleCount = settings.Rules.Count,
        settings.Routing,
        Security = new
        {
            settings.Security.RequireAuthentication,
            settings.Security.HttpsEnabled,
            settings.Security.SessionTimeoutMinutes
        }
    };

    public static IReadOnlyList<StructuredLogEntry> SanitizeLogs(IEnumerable<StructuredLogEntry> logs) => logs
        .Select(log => log with
        {
            Message = LogRedactor.RedactForDiagnostics(log.Message),
            SourceId = Opaque(log.SourceId)
        })
        .ToArray();

    private static string? Opaque(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
    }
}
