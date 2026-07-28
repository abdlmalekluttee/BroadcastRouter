using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using Microsoft.Data.Sqlite;

namespace BroadcastRouter.Infrastructure;

public sealed class SqliteDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteDataStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            CREATE TABLE IF NOT EXISTS settings (
                profile TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                valid INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS sources (
                source_id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ports (
                port_id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                mapping_fingerprint TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS routes (
                source_id TEXT PRIMARY KEY,
                port_id TEXT NULL,
                state TEXT NOT NULL,
                locked INTEGER NOT NULL,
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS route_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id TEXT NOT NULL,
                previous_state TEXT NULL,
                new_state TEXT NOT NULL,
                detail TEXT NULL,
                timestamp_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                level TEXT NOT NULL,
                category TEXT NOT NULL,
                message TEXT NOT NULL,
                source_id TEXT NULL,
                correlation_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_logs_timestamp ON logs(timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_logs_category ON logs(category);
            CREATE INDEX IF NOT EXISTS ix_history_source ON route_history(source_id, timestamp_utc DESC);
            """, cancellationToken);
    }

    public async Task<OperatorSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM settings WHERE profile='active' AND valid=1";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value)) return new OperatorSettings();
        try { return JsonSerializer.Deserialize<OperatorSettings>(value, JsonOptions) ?? new OperatorSettings(); }
        catch (JsonException)
        {
            await using var fallback = connection.CreateCommand();
            fallback.CommandText = "SELECT json FROM settings WHERE profile='last-valid' AND valid=1";
            var backup = await fallback.ExecuteScalarAsync(cancellationToken) as string;
            return string.IsNullOrWhiteSpace(backup)
                ? new OperatorSettings()
                : JsonSerializer.Deserialize<OperatorSettings>(backup, JsonOptions) ?? new OperatorSettings();
        }
    }

    public async Task SaveSettingsAsync(OperatorSettings settings, CancellationToken cancellationToken = default)
    {
        NormalizeSettings(settings);
        ValidateSettings(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var backup = connection.CreateCommand())
            {
                backup.Transaction = (SqliteTransaction)transaction;
                backup.CommandText = """
                    INSERT INTO settings(profile,json,updated_utc,valid)
                    SELECT 'last-valid',json,updated_utc,valid FROM settings WHERE profile='active'
                    ON CONFLICT(profile) DO UPDATE SET json=excluded.json,updated_utc=excluded.updated_utc,valid=excluded.valid;
                    """;
                await backup.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO settings(profile,json,updated_utc,valid) VALUES('active',$json,$now,1)
                    ON CONFLICT(profile) DO UPDATE SET json=excluded.json,updated_utc=excluded.updated_utc,valid=1;
                    """;
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task UpsertSourceAsync(DiscoveredSource source, CancellationToken cancellationToken = default) =>
        await UpsertJsonAsync("sources", "source_id", source.Identity.Value, "last_seen_utc", source, cancellationToken);

    public async Task<IReadOnlyList<DiscoveredSource>> LoadSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM sources ORDER BY last_seen_utc, source_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sources = new List<DiscoveredSource>();
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                var source = JsonSerializer.Deserialize<DiscoveredSource>(reader.GetString(0), JsonOptions);
                if (source is not null) sources.Add(source);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException) { }
        }
        return sources;
    }

    public async Task DeleteSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sources WHERE source_id=$id";
            command.Parameters.AddWithValue("$id", sourceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task UpsertPortAsync(DeckLinkPort port, CancellationToken cancellationToken = default)
    {
        var fingerprint = $"{port.PersistentId}|{port.DeviceGroupId}|{port.FfmpegName}|{port.ModelName}|{port.CardIndex}|{port.SubdeviceIndex}|{port.TopologicalId}|{port.PciLocation}";
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ports(port_id,json,mapping_fingerprint,last_seen_utc) VALUES($id,$json,$fingerprint,$now)
                ON CONFLICT(port_id) DO UPDATE SET json=excluded.json,mapping_fingerprint=excluded.mapping_fingerprint,last_seen_utc=excluded.last_seen_utc;
                """;
            command.Parameters.AddWithValue("$id", port.StableId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(port, JsonOptions));
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task SaveRouteAsync(RuntimeRoute route, RouteState? previousState = null, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO routes(source_id,port_id,state,locked,json,updated_utc) VALUES($id,$port,$state,$locked,$json,$now)
                    ON CONFLICT(source_id) DO UPDATE SET port_id=excluded.port_id,state=excluded.state,locked=excluded.locked,json=excluded.json,updated_utc=excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("$id", route.SourceId);
                command.Parameters.AddWithValue("$port", (object?)route.PortId ?? DBNull.Value);
                command.Parameters.AddWithValue("$state", route.State.ToString());
                command.Parameters.AddWithValue("$locked", route.Locked ? 1 : 0);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(route, JsonOptions));
                command.Parameters.AddWithValue("$now", route.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            if (previousState != route.State)
            {
                await using var history = connection.CreateCommand();
                history.Transaction = (SqliteTransaction)transaction;
                history.CommandText = "INSERT INTO route_history(source_id,previous_state,new_state,detail,timestamp_utc) VALUES($id,$previous,$state,$detail,$now)";
                history.Parameters.AddWithValue("$id", route.SourceId);
                history.Parameters.AddWithValue("$previous", previousState?.ToString() ?? (object)DBNull.Value);
                history.Parameters.AddWithValue("$state", route.State.ToString());
                history.Parameters.AddWithValue("$detail", (object?)route.FailureMessage ?? DBNull.Value);
                history.Parameters.AddWithValue("$now", route.UpdatedAt.ToString("O"));
                await history.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<RuntimeRoute>> LoadRoutesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM routes ORDER BY updated_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var routes = new List<RuntimeRoute>();
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                var route = JsonSerializer.Deserialize<RuntimeRoute>(reader.GetString(0), JsonOptions);
                if (route is not null) routes.Add(route);
            }
            catch (JsonException) { }
        }
        return routes;
    }

    public async Task WriteLogAsync(string level, string category, string message, string? sourceId = null, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var redacted = LogRedactor.Redact(message);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO logs(timestamp_utc,level,category,message,source_id,correlation_id) VALUES($now,$level,$category,$message,$source,$correlation)";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$level", level);
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$message", redacted);
            command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$correlation", (object?)correlationId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<StructuredLogEntry>> ReadLogsAsync(string? search = null, int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(search)
            ? "SELECT id,timestamp_utc,level,category,message,source_id,correlation_id FROM logs ORDER BY id DESC LIMIT $limit"
            : "SELECT id,timestamp_utc,level,category,message,source_id,correlation_id FROM logs WHERE message LIKE $search OR category LIKE $search OR source_id LIKE $search ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$search", $"%{search}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var logs = new List<StructuredLogEntry>();
        while (await reader.ReadAsync(cancellationToken))
            logs.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return logs;
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString() ?? "unknown";
    }

    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var source = await OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task UpsertJsonAsync<T>(string table, string keyColumn, string key, string timestampColumn, T value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {table}({keyColumn},json,{timestampColumn}) VALUES($id,$json,$now) ON CONFLICT({keyColumn}) DO UPDATE SET json=excluded.json,{timestampColumn}=excluded.{timestampColumn};";
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, JsonOptions));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateSettings(OperatorSettings settings)
    {
        if (settings.SchemaVersion < 1) throw new InvalidOperationException("Settings schema version is invalid.");
        ValidateOptionalPath(settings.MediaTools.FfmpegPath, "FFmpeg executable");
        ValidateOptionalPath(settings.MediaTools.FfprobePath, "FFprobe executable");
        ValidateOptionalPath(settings.MediaTools.FfplayPath, "FFplay executable");
        if (settings.WowzaServers.GroupBy(x => x.ServerId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Wowza server IDs must be unique.");
        foreach (var server in settings.WowzaServers)
        {
            if (string.IsNullOrWhiteSpace(server.ServerId)) throw new InvalidOperationException("Every Wowza server requires a stable ID.");
            if (!Uri.TryCreate(server.ManagementUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Wowza server {server.ServerId} has an invalid management URL.");
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException($"Wowza server {server.ServerId} must not embed credentials in its management URL.");
            if (string.IsNullOrWhiteSpace(server.RtspHost)) throw new InvalidOperationException($"Wowza server {server.ServerId} requires an RTSP host.");
            if (server.RtspPort is < 1 or > 65535) throw new InvalidOperationException($"Wowza server {server.ServerId} has an invalid RTSP port.");
            if (string.IsNullOrWhiteSpace(server.Applications)) throw new InvalidOperationException($"Wowza server {server.ServerId} requires at least one application.");
            if (string.IsNullOrWhiteSpace(server.ApplicationInstances)) throw new InvalidOperationException($"Wowza server {server.ServerId} requires at least one application instance.");
            if (server.PollingIntervalSeconds is < 1 or > 300) throw new InvalidOperationException($"Wowza server {server.ServerId} polling interval must be between 1 and 300 seconds.");
            if (server.ConnectionTimeoutSeconds is < 2 or > 60) throw new InvalidOperationException($"Wowza server {server.ServerId} connection timeout must be between 2 and 60 seconds.");
            RtspUrlGenerator.ValidateTemplate(server.RtspUrlTemplate);
        }
        if (settings.ManualSources.GroupBy(x => x.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Manual source IDs must be unique.");
        foreach (var source in settings.ManualSources)
        {
            if (string.IsNullOrWhiteSpace(source.StableId)) throw new InvalidOperationException("Every manual source requires a stable ID.");
            if (string.IsNullOrWhiteSpace(source.FriendlyName)) throw new InvalidOperationException($"Manual source {source.StableId} requires a name.");
            if (!Uri.TryCreate(source.RtspUrl, UriKind.Absolute, out var rtsp) || !rtsp.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Manual source {source.FriendlyName} requires an absolute RTSP URL.");
            if (!string.IsNullOrEmpty(rtsp.UserInfo))
                throw new InvalidOperationException($"Manual source {source.FriendlyName} must not embed credentials in its RTSP URL because settings are persisted.");
        }
        if (settings.Presets.Count == 0) throw new InvalidOperationException("At least one output preset is required.");
        if (settings.Presets.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Preset IDs must be unique.");
        foreach (var preset in settings.Presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.Name)) throw new InvalidOperationException("Every preset requires an ID and name.");
            if (preset.Width <= 0 || preset.Height <= 0) throw new InvalidOperationException($"Preset {preset.Id} requires a positive raster size.");
            if (preset.FrameRateNumerator <= 0 || preset.FrameRateDenominator <= 0) throw new InvalidOperationException($"Preset {preset.Id} requires a positive frame rate.");
            if (string.IsNullOrWhiteSpace(preset.PixelFormat)) throw new InvalidOperationException($"Preset {preset.Id} requires a pixel format.");
            if (preset.BufferSizeMegabytes is < 1 or > 4096) throw new InvalidOperationException($"Preset {preset.Id} buffer size must be between 1 and 4096 MB.");
            if (preset.RtspTransport is not ("tcp" or "udp")) throw new InvalidOperationException($"Preset {preset.Id} has an unsupported RTSP transport.");
            if (preset.AspectHandling is not ("Fit" or "Fill" or "Stretch")) throw new InvalidOperationException($"Preset {preset.Id} has an unsupported aspect-handling mode.");
            if (preset.StandbyMode == FallbackMode.StandbySource)
            {
                if (!Uri.TryCreate(preset.StandbyValue, UriKind.Absolute, out var standby) || !standby.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Preset {preset.Id} requires an absolute RTSP standby-source URL.");
                if (!string.IsNullOrEmpty(standby.UserInfo))
                    throw new InvalidOperationException($"Preset {preset.Id} must not embed credentials in its standby RTSP URL because settings are persisted.");
            }
            if (preset.StandbyMode is FallbackMode.File or FallbackMode.FreezeLastFrame)
            {
                if (string.IsNullOrWhiteSpace(preset.StandbyValue)) throw new InvalidOperationException($"Preset {preset.Id} requires a standby media path.");
                ValidateOptionalPath(preset.StandbyValue, $"Preset {preset.Id} standby media");
            }
        }
        var presetIds = settings.Presets.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (settings.Rules.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Routing rule IDs must be unique.");
        foreach (var rule in settings.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name)) throw new InvalidOperationException("Every routing rule requires an ID and name.");
            if (!presetIds.Contains(rule.PresetId)) throw new InvalidOperationException($"Routing rule {rule.Name} references missing preset {rule.PresetId}.");
            RoutingRuleEvaluator.ValidatePattern(rule.ServerPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.ApplicationPattern);
            RoutingRuleEvaluator.ValidatePattern(rule.InstancePattern);
            RoutingRuleEvaluator.ValidatePattern(rule.StreamPattern);
        }
        if (settings.DeckLinkCardOverrides.GroupBy(x => x.DeviceGroupId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("DeckLink physical-card identities must be unique.");
        if (settings.DeckLinkCardOverrides.Any(x => string.IsNullOrWhiteSpace(x.DeviceGroupId)))
            throw new InvalidOperationException("Every DeckLink card name requires a persistent physical-card identity.");
        if (settings.DeckLinkCardOverrides.Where(x => !string.IsNullOrWhiteSpace(x.FriendlyName))
            .GroupBy(x => x.FriendlyName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("DeckLink card names must be unique so output choices remain unambiguous.");
        if (settings.DeckLinkPortOverrides.GroupBy(x => x.StableId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("DeckLink connector mappings must have unique stable IDs.");
        if (settings.DeckLinkPortOverrides.Any(x => string.IsNullOrWhiteSpace(x.StableId)))
            throw new InvalidOperationException("Every DeckLink connector mapping requires a stable ID.");
        foreach (var port in settings.DeckLinkPortOverrides)
        {
            if (port.IsOutputPort && port.StandbyEnabled && !presetIds.Contains(port.StandbyPresetId))
                throw new InvalidOperationException($"Output connector {port.FriendlyName} requires a valid standby preset.");
            ValidateOptionalPath(port.StandbyLogoPath, $"Output connector {port.FriendlyName} standby logo");
            if (port.StandbyLabel.Length > 80 || port.StandbyLabel.Any(character => char.IsControl(character)
                    || character is ':' or ';' or ',' or '[' or ']' or '\\' or '\'' or '"' or '%'))
                throw new InvalidOperationException($"Output connector {port.FriendlyName} standby label contains unsupported filter characters or exceeds 80 characters.");
        }
        var outputPortIds = settings.DeckLinkPortOverrides.Where(x => x.IsOutputPort)
            .Select(x => x.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (settings.ManualSources.Any(source => !string.IsNullOrWhiteSpace(source.FixedPortId) && !outputPortIds.Contains(source.FixedPortId)))
            throw new InvalidOperationException("A manual source references a connector that is not marked as an output port.");
        if (settings.Rules.Any(rule => !string.IsNullOrWhiteSpace(rule.FixedPortId) && !outputPortIds.Contains(rule.FixedPortId)))
            throw new InvalidOperationException("A routing rule references a connector that is not marked as an output port.");
        if (settings.Routing.ReservationGraceSeconds < 0 || settings.Routing.StableRestoreSeconds < 0 || settings.Routing.StallTimeoutSeconds <= 0
            || settings.Routing.FirstProgressTimeoutSeconds <= 0 || settings.Routing.GracefulStopSeconds <= 0 || settings.Routing.MaxRetryAttempts < 0)
            throw new InvalidOperationException("Routing and recovery timeouts contain an invalid value.");
        if (settings.Routing.RetryDelaysSeconds.Length == 0 || settings.Routing.RetryDelaysSeconds.Any(value => value < 0))
            throw new InvalidOperationException("Retry delays require one or more non-negative values.");
        NetworkAccessPolicy.ValidateExposure(settings.Security.BindAddress, settings.Security.RequireAuthentication);
        NetworkAccessPolicy.Validate(settings.Security.AllowedNetworks);
        _ = NetworkAccessPolicy.ParseTrustedProxies(settings.Security.TrustedProxies);
        if (settings.Security.Port is < 1 or > 65535) throw new InvalidOperationException("Security port must be between 1 and 65535.");
        if (settings.Security.SessionTimeoutMinutes is < 5 or > 1440) throw new InvalidOperationException("Session timeout must be between 5 and 1440 minutes.");
    }

    private static void NormalizeSettings(OperatorSettings settings)
    {
        settings.SchemaVersion = Math.Max(settings.SchemaVersion, 5);
        settings.MediaTools.FfmpegPath = settings.MediaTools.FfmpegPath.Trim();
        settings.MediaTools.FfprobePath = settings.MediaTools.FfprobePath.Trim();
        settings.MediaTools.FfplayPath = settings.MediaTools.FfplayPath.Trim();
        foreach (var server in settings.WowzaServers)
        {
            server.FriendlyName = server.FriendlyName.Trim();
            server.ServerId = server.ServerId.Trim();
            server.ManagementUrl = server.ManagementUrl.Trim();
            server.Username = server.Username.Trim();
            server.RtspHost = server.RtspHost.Trim();
            server.Applications = server.Applications.Trim();
            server.ApplicationInstances = server.ApplicationInstances.Trim();
            server.RtspUrlTemplate = server.RtspUrlTemplate.Trim();
        }
        foreach (var source in settings.ManualSources)
        {
            source.StableId = source.StableId.Trim();
            source.FriendlyName = source.FriendlyName.Trim();
            source.RtspUrl = source.RtspUrl.Trim();
            source.FixedPortId = source.FixedPortId.Trim();
        }
        foreach (var preset in settings.Presets)
        {
            preset.Id = preset.Id.Trim();
            preset.Name = preset.Name.Trim();
            preset.PixelFormat = preset.PixelFormat.Trim();
            preset.StandbyValue = preset.StandbyValue.Trim();
        }
        foreach (var rule in settings.Rules)
        {
            rule.Id = rule.Id.Trim();
            rule.Name = rule.Name.Trim();
            rule.PresetId = rule.PresetId.Trim();
            rule.FixedPortId = rule.FixedPortId.Trim();
            rule.PortGroup = rule.PortGroup.Trim();
        }
        foreach (var card in settings.DeckLinkCardOverrides)
        {
            card.DeviceGroupId = card.DeviceGroupId.Trim();
            card.FriendlyName = card.FriendlyName.Trim();
        }
        foreach (var port in settings.DeckLinkPortOverrides)
        {
            port.StableId = port.StableId.Trim();
            port.FriendlyName = port.FriendlyName.Trim();
            port.PortGroup = port.PortGroup.Trim();
            port.StandbyPresetId = port.StandbyPresetId.Trim();
            port.StandbyLogoPath = port.StandbyLogoPath.Trim();
            port.StandbyLabel = port.StandbyLabel.Trim();
        }
        settings.Security.BindAddress = settings.Security.BindAddress.Trim();
        settings.Security.AllowedNetworks = settings.Security.AllowedNetworks.Trim();
        settings.Security.TrustedProxies = settings.Security.TrustedProxies.Trim();
    }

    private static void ValidateOptionalPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { _ = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{label} path is invalid.", ex);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new ReadOnlyStringSetConverter());
        return options;
    }

    private sealed class ReadOnlyStringSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var values = JsonSerializer.Deserialize<string[]>(ref reader, options) ?? [];
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(), options);
    }
}
