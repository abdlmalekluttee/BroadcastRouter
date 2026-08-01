using System.Text.Json;

namespace BroadcastRouter.Infrastructure;

public sealed record DeckLinkIdentityProbeResult(
    bool Success,
    bool TimedOut,
    IReadOnlyList<DeckLinkHardwareIdentity> Identities,
    string? Error);

/// <summary>
/// Runs the native DeckLink COM enumeration in a disposable helper process.
/// Desktop Video can occasionally block inside a driver call; isolating that
/// call prevents the routing coordinator and service shutdown from blocking.
/// </summary>
public static class DeckLinkIdentityProcessProbe
{
    public const string CommandArgument = "--enumerate-decklink-identities-json";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DeckLinkIdentityProbeResult> EnumerateAsync(
        string serverExecutable,
        CancellationToken cancellationToken)
    {
        var result = await ExternalCommandRunner.RunAsync(
            serverExecutable,
            [CommandArgument],
            DefaultTimeout,
            cancellationToken,
            containOnWindows: true).ConfigureAwait(false);

        if (!result.Started)
            return new(false, false, [], $"DeckLink identity helper did not start: {result.StartError}");
        if (result.TimedOut)
            return new(false, true, [], $"DeckLink identity helper exceeded the {DefaultTimeout.TotalSeconds:0}-second deadline.");
        if (result.ExitCode != 0)
            return new(false, false, [], $"DeckLink identity helper exited with code {result.ExitCode}: {LogRedactor.Redact(result.CombinedOutput)}");

        try
        {
            var identities = JsonSerializer.Deserialize<DeckLinkHardwareIdentity[]>(result.StandardOutput,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return new(true, false, identities, null);
        }
        catch (JsonException ex)
        {
            return new(false, false, [], $"DeckLink identity helper returned invalid JSON: {ex.Message}");
        }
    }
}
