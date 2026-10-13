using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BroadcastRouter.Domain;
using Microsoft.Data.Sqlite;

namespace BroadcastRouter.Infrastructure;

public sealed record BenchProcessEvidence(int ProcessId, string Name, string? CommandLine);

public static class BenchReleasePreflight
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public static async Task EnsureReleasedAsync(string productionDatabase, BenchOutputGuard guard,
        Func<CancellationToken, Task<IReadOnlyList<BenchProcessEvidence>>> readProcesses,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var before = await ReadReleasedAsync(productionDatabase, guard, cancellationToken).ConfigureAwait(false);
            var processes = await readProcesses(cancellationToken).ConfigureAwait(false);
            if (processes is null) throw Refused("process inventory is missing");
            CheckOwners(guard, before.Ports, processes);
            var after = await ReadReleasedAsync(productionDatabase, guard, cancellationToken).ConfigureAwait(false);
            if (before.SettingsJson != after.SettingsJson) throw Refused("configuration changed during preflight");
        }
        catch (BenchSafetyException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or TimeoutException)
        {
            // Do not echo database contents, command lines or operational identifiers.
            throw Refused("release evidence could not be read or validated (" + ex.GetType().Name + ")");
        }
    }

    public static async Task<IReadOnlyList<BenchProcessEvidence>> ReadWindowsProcessesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw Refused("Windows process evidence is required");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        // Constant, read-only command. No process names are killed, no script policy is changed,
        // and no untrusted values are interpolated. The helper is hidden, bounded and contained.
        const string command = "$ErrorActionPreference='Stop'; @(Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine) | ConvertTo-Json -Depth 3 -Compress";
        var result = await ExternalCommandRunner.RunAsync(executable, ["-NoProfile", "-NonInteractive", "-Command", command],
            TimeSpan.FromSeconds(8), cancellationToken, containOnWindows: true).ConfigureAwait(false);
        if (!result.Started || result.TimedOut || result.ExitCode != 0) throw Refused("Windows process inventory failed");
        return JsonSerializer.Deserialize<BenchProcessEvidence[]>(result.StandardOutput, Json)
            ?? throw Refused("Windows process inventory was empty");
    }

    private static async Task<ReleaseSnapshot> ReadReleasedAsync(string database, BenchOutputGuard guard, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(database) || !File.Exists(database)) throw Refused("production database is unavailable");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var settingsRows = await Rows("SELECT json FROM settings WHERE profile='active' AND valid=1", 1);
        if (settingsRows.Count != 1) throw Refused("active settings are unavailable");
        using var settings = JsonDocument.Parse(settingsRows[0]);
        if (!settings.RootElement.TryGetProperty("deckLinkPortOverrides", out var overrides) || overrides.ValueKind != JsonValueKind.Array)
            throw Refused("explicit output selections are missing");
        var ids = guard.Bindings.Select(b => b.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in guard.Bindings)
        {
            var matches = overrides.EnumerateArray().Where(row => row.TryGetProperty("stableId", out var id)
                && id.GetString() == binding.StableId).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("isOutputPort", out var enabled) || enabled.ValueKind != JsonValueKind.False)
                throw Refused("all five output selections must be explicitly disabled");
        }
        var ports = CanonicalInventory((await Rows("SELECT json FROM ports", 256)).Select(row =>
            JsonSerializer.Deserialize<DeckLinkPort>(row, Json) ?? throw Refused("device identity missing")).ToArray());
        foreach (var binding in guard.Bindings)
        {
            var matches = ports.Where(p => p.StableId == binding.StableId).ToArray();
            if (matches.Length != 1 || ports.Count(p => p.DeviceHandle == binding.DeviceHandle) != 1)
                throw Refused("device inventory is missing or ambiguous");
            try { guard.ValidateIdentity(matches[0]); }
            catch (BenchSafetyException) { throw Refused("physical identity no longer matches the approved allowlist"); }
            ids.UnionWith(matches[0].PreviousStableIds ?? []);
        }
        foreach (var mapping in overrides.EnumerateArray())
        {
            if (mapping.TryGetProperty("stableId", out var id) && id.ValueKind == JsonValueKind.String
                && ids.Contains(id.GetString()!)
                && (!mapping.TryGetProperty("isOutputPort", out var enabled) || enabled.ValueKind != JsonValueKind.False))
                throw Refused("a current or legacy output selection is still enabled");
        }
        foreach (var row in await Rows("SELECT json FROM routes", 512))
        {
            using var route = JsonDocument.Parse(row);
            var root = route.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
                || !root.TryGetProperty("sourceId", out var sourceId) || sourceId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sourceId.GetString())
                || !root.TryGetProperty("state", out var state) || !IsDefinedRouteState(state))
                throw Refused("route evidence is incomplete or noncanonical");
            foreach (var property in new[] { "portId", "desiredPortId" })
            {
                // Persisted runtime routes contain both fields, including explicit
                // nulls. Never interpret missing or corrupt evidence as released.
                if (!root.TryGetProperty(property, out var portId)
                    || portId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    throw Refused("route port evidence is incomplete or noncanonical");
                if (portId.ValueKind == JsonValueKind.Null) continue;
                var id = portId.GetString();
                if (string.IsNullOrWhiteSpace(id)) throw Refused("route port identity is empty");
                if (ids.Contains(id)) throw Refused("a test connector still carries a current or saved route");
                if (ports.Count(p => p.StableId.Equals(id, StringComparison.OrdinalIgnoreCase)
                    || (p.PreviousStableIds?.Contains(id, StringComparer.OrdinalIgnoreCase) ?? false)) != 1)
                    throw Refused("route port identity cannot be resolved unambiguously");
            }
        }
        return new(settingsRows[0], ports);

        async Task<List<string>> Rows(string sql, int limit)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = 1;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<string>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows.Count >= limit || reader.IsDBNull(0)) throw Refused("evidence exceeds its bounded read or is incomplete");
                var value = reader.GetString(0);
                if (value.Length > 1024 * 1024) throw Refused("evidence row exceeds its bounded read");
                rows.Add(value);
            }
            return rows;
        }
    }

    private static bool IsDefinedRouteState(JsonElement state) => state.ValueKind switch
    {
        // SqliteDataStore writes numeric enums. Also accept the older explicit
        // string representation, but never unknown values or missing fields.
        JsonValueKind.Number => state.TryGetInt32(out var number) && Enum.IsDefined((RouteState)number),
        JsonValueKind.String => Enum.TryParse<RouteState>(state.GetString(), true, out var value) && Enum.IsDefined(value),
        _ => false
    };

    private static DeckLinkPort[] CanonicalInventory(DeckLinkPort[] stored)
    {
        if (stored.Any(p => string.IsNullOrWhiteSpace(p.StableId))
            || stored.Select(p => p.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != stored.Length)
            throw Refused("device identifiers are missing or duplicated");
        return stored.GroupBy(p => p.DeviceHandle, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            if (group.Count() == 1) return group.Single();
            // Old versions retained their pre-migration discovery rows. Recognize
            // ONLY the exact generated name hash explicitly linked by a current
            // persistent record, with all physical identity fields agreeing.
            // This is a read-only view: no deletion or migration of production data.
            var canonical = group.Where(p => p.PersistentId is { Length: > 2 }
                && p.PersistentId.StartsWith("0x", StringComparison.Ordinal)
                && p.StableId == "DECKLINK-PERSISTENT-" + p.PersistentId[2..]).ToArray();
            if (group.Count() != 2 || canonical.Length != 1 || string.IsNullOrWhiteSpace(group.Key))
                throw Refused("device inventory is missing or ambiguous");
            var current = canonical[0];
            var retired = group.Single(p => p.StableId != current.StableId);
            if (retired.StableId != DeckLinkIdentityResolver.LegacyStableId(current.DeviceHandle!)
                || !(current.PreviousStableIds?.Contains(retired.StableId, StringComparer.Ordinal) ?? false)
                || retired.IsOutputPort || retired.Reserved
                || retired.DeviceHandle != current.DeviceHandle || retired.FfmpegName != current.FfmpegName
                || current.FfmpegName != current.DeviceHandle || retired.PersistentId != current.PersistentId
                || retired.DeviceGroupId != current.DeviceGroupId || retired.SubdeviceIndex != current.SubdeviceIndex
                || retired.ModelName != current.ModelName || retired.TopologicalId != current.TopologicalId)
                throw Refused("device inventory is missing or ambiguous");
            return current;
        }).ToArray();
    }

    private static void CheckOwners(BenchOutputGuard guard, DeckLinkPort[] ports, IReadOnlyList<BenchProcessEvidence> processes)
    {
        var handles = guard.Bindings.Select(p => p.DeviceHandle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedHandles = ports.Where(p => !handles.Contains(p.FfmpegName) && p.FfmpegName == p.DeviceHandle)
            .Select(p => p.FfmpegName).ToHashSet(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            if (string.IsNullOrWhiteSpace(process.Name)) throw Refused("process identity is missing");
            if (string.IsNullOrWhiteSpace(process.CommandLine))
            {
                if (process.Name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) throw Refused("media owner command line is unreadable");
                continue;
            }
            var args = SplitCommandLine(process.CommandLine);
            // Nested muxers embed destinations inside a token (and may select a
            // device by display name). They cannot prove a connector is free.
            if (Enumerable.Range(0, Math.Max(0, args.Length - 1)).Any(i => args[i] == "-f"
                && (args[i + 1].Equals("tee", StringComparison.OrdinalIgnoreCase)
                    || args[i + 1].Equals("fifo", StringComparison.OrdinalIgnoreCase)))
                || args.Any(arg => arg.Contains("decklink", StringComparison.OrdinalIgnoreCase)
                    && arg.Contains('[') && arg.Contains(']')))
                throw Refused("nested or ambiguous media destinations cannot certify release");
            if (args.Any(handles.Contains)) throw Refused("a process still targets a test connector");
            var outputs = Enumerable.Range(0, Math.Max(0, args.Length - 1)).Count(i => args[i] == "-f" && args[i + 1] == "decklink");
            if (outputs > 0 && (outputs != 1 || args.Length == 0 || !protectedHandles.Contains(args[^1])))
                throw Refused("DeckLink owner uses an ambiguous or non-handle target");
        }
    }

    private static string[] SplitCommandLine(string command)
    {
        if (!OperatingSystem.IsWindows()) throw Refused("Windows argument parsing is required");
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero || count < 1) throw Refused("process command line cannot be parsed");
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    private static BenchSafetyException Refused(string reason) => new("Bench preflight refused: " + reason + ".");
    private sealed record ReleaseSnapshot(string SettingsJson, DeckLinkPort[] Ports);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
