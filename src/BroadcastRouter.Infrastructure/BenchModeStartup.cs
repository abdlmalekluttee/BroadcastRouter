using System.Text.Json;
using System.Net;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record BenchStartupPolicy(string ProductionApplicationDirectory, string ProductionDatabase,
    IReadOnlyList<BenchPortBinding> Outputs);

public static class BenchModeStartup
{
    public static Task<BenchOutputGuard> LoadAndVerifyAsync(string policyFile, string applicationDirectory,
        string dataDirectory, Func<CancellationToken, Task<IReadOnlyList<BenchProcessEvidence>>> readProcesses,
        CancellationToken cancellationToken = default) => LoadAndVerifyAsync(policyFile, applicationDirectory,
            dataDirectory, readProcesses, File.GetAttributes, cancellationToken);

    internal static async Task<BenchOutputGuard> LoadAndVerifyAsync(string policyFile, string applicationDirectory,
        string dataDirectory, Func<CancellationToken, Task<IReadOnlyList<BenchProcessEvidence>>> readProcesses,
        Func<string, FileAttributes> readAttributes, CancellationToken cancellationToken = default)
    {
        try
        {
            var app = Absolute(applicationDirectory);
            var data = Absolute(dataDirectory);
            var file = Absolute(policyFile);
            if (!Within(data, app) || data == app || !Within(file, app)) throw Refused("policy and data must be inside the separate candidate folder");
            var policy = JsonSerializer.Deserialize<BenchStartupPolicy>(await File.ReadAllTextAsync(file, cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw Refused("policy is missing");
            var production = Absolute(policy.ProductionApplicationDirectory);
            var database = Absolute(policy.ProductionDatabase);
            if (!Within(database, production) || Within(app, production) || Within(production, app))
                throw Refused("candidate and production folders overlap or database is outside production");
            foreach (var path in new[] { app, data, file, production, database }) RejectReparsePoints(path, readAttributes);
            RejectLinkedWritableDescendants(data, readAttributes);
            if (policy.Outputs is null) throw Refused("allowlist is missing");
            var guard = new BenchOutputGuard(policy.Outputs);
            await BenchReleasePreflight.EnsureReleasedAsync(database, guard, readProcesses, cancellationToken).ConfigureAwait(false);
            return guard;
        }
        catch (BenchSafetyException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw Refused("policy or isolation evidence cannot be read (" + ex.GetType().Name + ")"); }
    }

    public static void VerifySettings(OperatorSettings settings)
    {
        if (!IPAddress.TryParse(settings.Security.BindAddress, out var address) || !IPAddress.IsLoopback(address))
            throw Refused("bench HTTP must bind to a literal loopback address");
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Refused("absolute paths are required");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string path, Func<string, FileAttributes> readAttributes)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (readAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Refused("reparse-point paths are not accepted for a shared-machine bench");
    }

    private static void RejectLinkedWritableDescendants(string data, Func<string, FileAttributes> readAttributes)
    {
        if (!Directory.Exists(data)) return;
        var pending = new Stack<string>(); pending.Push(data);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 10_000) throw Refused("candidate data exceeds the bounded isolation inspection");
                var attributes = readAttributes(path);
                // Inspect each entry before descending, including database/WAL
                // files and existing telemetry segments. Never traverse a link.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw Refused("reparse-point writable descendants are not accepted for a shared-machine bench");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }

    private static BenchSafetyException Refused(string reason) => new("Bench startup refused: " + reason + ".");
}
