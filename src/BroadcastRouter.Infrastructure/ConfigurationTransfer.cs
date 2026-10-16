using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Microsoft.Data.Sqlite;

namespace BroadcastRouter.Infrastructure;

/// <summary>Offline configuration transfer. Never copies a database or decrypts credentials.</summary>
public static class ConfigurationTransfer
{
    private static readonly JsonSerializerOptions SourceJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions TransferJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<string> ExportAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var options = new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(options.ToString());
        await connection.OpenAsync(cancellationToken);
        // A deferred read transaction gives settings and saved intent one snapshot,
        // without schema initialization, writes, or a source-database copy.
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM settings WHERE profile='active' AND valid=1";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("No valid active configuration to export.");
        var settings = JsonSerializer.Deserialize<OperatorSettings>(json, SourceJson)
            ?? throw new InvalidDataException("Invalid source configuration.");
        if (settings.SchemaVersion > 6) throw new InvalidDataException("Unsupported source configuration version.");
        command.CommandText = "SELECT json FROM routes ORDER BY source_id";
        var routes = new List<SavedRouteTransfer>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var route = JsonSerializer.Deserialize<RuntimeRoute>(reader.GetString(0), SourceJson)
                    ?? throw new InvalidDataException("Invalid saved route.");
                route = DesiredRoutePolicy.MigrateLegacy(route);
                if (DesiredRoutePolicy.HasSavedAssignment(route) && route.State != RouteState.Released)
                    routes.Add(new(route.SourceId, route.SourceName, route.DesiredPortId!, route.DesiredPortName,
                        route.PresetId, route.AssignmentMode, route.Locked, route.Priority,
                        route.ReserveWhileOffline, route.AllowTemporaryUse, route.HoldOutputWhilePublisherLive));
            }
        }
        var servers = settings.WowzaServers.Select(server => new WowzaProfileTransfer(
            server.FriendlyName, server.ServerId, SafeUrl(server.ManagementUrl), server.Username,
            server.ValidateTlsCertificate, server.RtspHost, server.RtspPort, server.Applications,
            server.ApplicationInstances, SafeUrl(server.RtspUrlTemplate), server.PollingIntervalSeconds,
            server.ConnectionTimeoutSeconds, server.Enabled, server.Priority)).ToList();
        foreach (var source in settings.ManualSources) source.RtspUrl = SafeUrl(source.RtspUrl);
        foreach (var preset in settings.Presets.Where(p => p.StandbyMode == FallbackMode.StandbySource || p.StandbyValue.Contains("://", StringComparison.Ordinal)))
            preset.StandbyValue = SafeUrl(preset.StandbyValue);
        var document = new ConfigurationTransferDocument(1, servers, settings.ManualSources,
            settings.SourceMediaProfiles, settings.SourceMediaMigrationVersion, settings.Presets, settings.Rules,
            settings.DeckLinkCardOverrides, settings.DeckLinkPortOverrides, settings.Routing, routes);
        return JsonSerializer.Serialize(document, TransferJson);
    }

    public static async Task ImportAsync(string json, string databasePath, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(databasePath);
        if (DestinationExists(destination))
            throw new IOException("Import requires a new database path; existing files are never replaced.");
        if (json.Length > 16 * 1024 * 1024) throw new InvalidDataException("Configuration transfer is too large.");
        ConfigurationTransferDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            ValidateTransferShape(parsed.RootElement, typeof(ConfigurationTransferDocument));
            document = JsonSerializer.Deserialize<ConfigurationTransferDocument>(json, TransferJson)!;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid configuration transfer JSON or unsupported fields."); }
        if (document.FormatVersion != 1) throw new InvalidDataException("Unsupported configuration transfer version.");
        var settings = new OperatorSettings
        {
            WowzaServers = document.WowzaServers.Select(server => new WowzaServerProfile
            {
                FriendlyName = server.FriendlyName,
                ServerId = server.ServerId,
                ManagementUrl = SafeUrl(server.ManagementUrl),
                Username = server.Username,
                ProtectedPassword = "",
                ValidateTlsCertificate = server.ValidateTlsCertificate,
                RtspHost = server.RtspHost,
                RtspPort = server.RtspPort,
                Applications = server.Applications,
                ApplicationInstances = server.ApplicationInstances,
                RtspUrlTemplate = SafeUrl(server.RtspUrlTemplate),
                PollingIntervalSeconds = server.PollingIntervalSeconds,
                ConnectionTimeoutSeconds = server.ConnectionTimeoutSeconds,
                Enabled = false,
                Priority = server.Priority
            }).ToList(),
            ManualSources = document.ManualSources,
            SourceMediaProfiles = document.SourceMediaProfiles,
            SourceMediaMigrationVersion = document.SourceMediaMigrationVersion,
            Presets = document.Presets,
            Rules = document.Rules,
            DeckLinkCardOverrides = document.DeckLinkCardOverrides,
            DeckLinkPortOverrides = document.DeckLinkPortOverrides,
            Routing = document.Routing,
            Security = new() { RequireAuthentication = true }
        };
        settings.Routing.AutomaticRoutingEnabled = false;
        foreach (var source in settings.ManualSources) { source.RtspUrl = SafeUrl(source.RtspUrl); source.Enabled = false; }
        foreach (var preset in settings.Presets.Where(p => p.StandbyMode == FallbackMode.StandbySource || p.StandbyValue.Contains("://", StringComparison.Ordinal)))
            preset.StandbyValue = SafeUrl(preset.StandbyValue);
        if (document.SavedRoutes.GroupBy(r => r.SourceId, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate saved route identities.");
        foreach (var route in document.SavedRoutes)
        {
            if (string.IsNullOrWhiteSpace(route.SourceId) || string.IsNullOrWhiteSpace(route.DesiredPortId)
                || route.AssignmentMode is not (AssignmentMode.Manual or AssignmentMode.Preconfigured or AssignmentMode.Rule or AssignmentMode.Fixed)
                || !settings.DeckLinkPortOverrides.Any(p => p.IsOutputPort
                    && string.Equals(p.StableId, route.DesiredPortId, StringComparison.OrdinalIgnoreCase))
                || !settings.Presets.Any(p => p.Id == route.PresetId))
                throw new InvalidDataException("Saved route does not reference an output port and preset in this configuration.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Build a private staging DB on the destination volume. Only the final
        // no-overwrite rename exposes it. A crash leaves the destination absent.
        var staging = destination + ".import-" + Guid.NewGuid().ToString("N");
        var store = new SqliteDataStore(staging);
        try
        {
            await store.InitializeAsync(cancellationToken);
            await store.SaveSettingsAsync(settings, cancellationToken);
            foreach (var route in document.SavedRoutes)
            {
                var intent = new RuntimeRoute(route.SourceId, route.SourceName, null, null, route.PresetId,
                    RouteState.WaitingForStream, route.AssignmentMode, route.Locked, route.Priority, 0,
                    null, null, null, 0, 0, null, SupervisionClock.System.UtcNow, null, null,
                    DesiredPortId: route.DesiredPortId, DesiredPortName: route.DesiredPortName,
                    ReserveWhileOffline: route.ReserveWhileOffline, AllowTemporaryUse: route.AllowTemporaryUse,
                    HoldOutputWhilePublisherLive: route.HoldOutputWhilePublisherLive);
                await store.SaveRouteAsync(intent, intent.State, cancellationToken);
            }
            await store.StopDiagnosticsAsync();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = staging, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0)
                    throw new IOException("Configuration checkpoint did not complete.");
            }
            ClearStagingPool(staging);
            cancellationToken.ThrowIfCancellationRequested();
            if (DestinationExists(destination)) throw new IOException("Destination changed during import; nothing replaced.");
            File.Move(staging, destination, overwrite: false);
        }
        finally
        {
            await store.StopDiagnosticsAsync();
            ClearStagingPool(staging);
            // Only this invocation's random staging files, never the destination.
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(staging + suffix);
        }
    }

    private static bool DestinationExists(string path) => new[] { "", "-wal", "-shm", "-journal" }
        .Any(suffix => File.Exists(path + suffix) || Directory.Exists(path + suffix));

    private static void ValidateTransferShape(JsonElement value, Type type, bool nullable = false)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullable || Nullable.GetUnderlyingType(type) is not null) return;
            throw new InvalidDataException("Configuration transfer contains a null required value.");
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsValueType || type == typeof(string)) return; // Serializer checks scalar types/ranges.
        var elementType = type.IsArray ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;
        if (elementType is not null)
        {
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Configuration list is not an array.");
            foreach (var item in value.EnumerateArray()) ValidateTransferShape(item, elementType);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Configuration object is missing.");
        var nullability = new NullabilityInfoContext();
        // Legacy source JSON may use application defaults. The transfer format
        // may not: every exported property, including nested mute/TLS fields,
        // must be present. Otherwise a partial file could silently alter intent.
        foreach (var property in type.GetProperties())
        {
            if (!value.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(property.Name), out var child))
                throw new InvalidDataException("Configuration transfer is missing a required field.");
            ValidateTransferShape(child, property.PropertyType, nullability.Create(property).ReadState == NullabilityState.Nullable);
        }
    }

    private static void ClearStagingPool(string path)
    {
        var options = new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true };
        using var connection = new SqliteConnection(options.ToString());
        SqliteConnection.ClearPool(connection);
        // StopDiagnostics opens the short-timeout diagnostic pool separately.
        options.DefaultTimeout = 1;
        using var diagnostics = new SqliteConnection(options.ToString());
        SqliteConnection.ClearPool(diagnostics);
    }

    private static string SafeUrl(string value)
    {
        // URL query tokens/fragments cannot safely be inferred as non-secret.
        // Refuse those exports rather than silently transfer a credential.
        if (value.Contains('?') || value.Contains('#'))
            throw new InvalidDataException("Connection URLs with query or fragment data require manual re-entry.");
        return Regex.Replace(value, @"(?<=://)[^/?#]*@", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

// Deliberate allowlist: no OperatorSettings, ProtectedPassword, Security,
// MediaTools, discovered source/port cache, audit, history, or runtime route graph.
public sealed record ConfigurationTransferDocument(
    int FormatVersion, List<WowzaProfileTransfer> WowzaServers, List<ManualSourceProfile> ManualSources,
    List<SourceMediaProfile> SourceMediaProfiles, int SourceMediaMigrationVersion,
    List<OutputPresetProfile> Presets, List<RoutingRuleProfile> Rules,
    List<DeckLinkCardOverride> DeckLinkCardOverrides, List<DeckLinkPortOverride> DeckLinkPortOverrides,
    RoutingPolicySettings Routing, List<SavedRouteTransfer> SavedRoutes);

public sealed record WowzaProfileTransfer(
    string FriendlyName, string ServerId, string ManagementUrl, string Username, bool ValidateTlsCertificate,
    string RtspHost, int RtspPort, string Applications, string ApplicationInstances, string RtspUrlTemplate,
    int PollingIntervalSeconds, int ConnectionTimeoutSeconds, bool Enabled, int Priority);

public sealed record SavedRouteTransfer(
    string SourceId, string SourceName, string DesiredPortId, string? DesiredPortName, string PresetId,
    AssignmentMode AssignmentMode, bool Locked, int Priority, bool ReserveWhileOffline,
    bool AllowTemporaryUse, bool HoldOutputWhilePublisherLive);
