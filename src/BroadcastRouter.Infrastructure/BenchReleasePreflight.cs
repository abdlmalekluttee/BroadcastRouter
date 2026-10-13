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
        var ids = guard.Bindings.Select(b => b.StableId).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in guard.Bindings)
        {
            var matches = overrides.EnumerateArray().Where(row => row.TryGetProperty("stableId", out var id)
                && id.GetString() == binding.StableId).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("isOutputPort", out var enabled) || enabled.ValueKind != JsonValueKind.False)
                throw Refused("all five output selections must be explicitly disabled");
        }
        var ports = (await Rows("SELECT json FROM ports", 256)).Select(row =>
            JsonSerializer.Deserialize<DeckLinkPort>(row, Json) ?? throw Refused("device identity missing")).ToArray();
        foreach (var binding in guard.Bindings)
        {
            var matches = ports.Where(p => p.StableId == binding.StableId).ToArray();
            if (matches.Length != 1 || ports.Count(p => p.DeviceHandle == binding.DeviceHandle) != 1)
                throw Refused("device inventory is missing or ambiguous");
            try { guard.ValidateIdentity(matches[0]); }
            catch (BenchSafetyException) { throw Refused("physical identity no longer matches the approved allowlist"); }
        }
        foreach (var row in await Rows("SELECT json FROM routes", 512))
        {
            using var route = JsonDocument.Parse(row);
            if (!route.RootElement.TryGetProperty("sourceId", out var sourceId) || string.IsNullOrWhiteSpace(sourceId.GetString())
                || !route.RootElement.TryGetProperty("state", out _)) throw Refused("route evidence is incomplete");
            foreach (var property in new[] { "portId", "desiredPortId" })
                if (route.RootElement.TryGetProperty(property, out var portId) && portId.ValueKind == JsonValueKind.String
                    && ids.Contains(portId.GetString()!)) throw Refused("a test connector still carries a current or saved route");
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
