using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class WowzaDiscoveryProvider(
    HttpClient httpClient,
    WowzaServerConfiguration server,
    ICredentialResolver credentialResolver, SupervisionClock? clock = null) : IStreamDiscoveryProvider
{
    public async Task<IReadOnlyList<DiscoveredSource>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!server.Enabled) return [];
        if (server.Applications.Count == 0 || server.ApplicationInstances.Count == 0)
            throw new JsonException("Wowza inventory has no configured application/instance scope; observation is unknown.");
        var credential = await credentialResolver.ResolveAsync(server.CredentialReference, cancellationToken).ConfigureAwait(false);
        var sources = new List<DiscoveredSource>();
        foreach (var application in server.Applications)
        foreach (var instance in server.ApplicationInstances)
        {
            using var document = await GetInstanceAsync(application, instance, credential, cancellationToken).ConfigureAwait(false);
            foreach (var observation in WowzaIncomingStreamParser.Parse(document.RootElement))
            {
                var identity = new SourceIdentity(server.ServerId, application, instance, observation.StreamName);
                sources.Add(new DiscoveredSource(
                    identity,
                    observation.StreamName,
                    RtspUrlGenerator.Generate(server, identity),
                    observation.PublisherConnected ? SourceState.PublisherActive : SourceState.PublisherDisconnected,
                    server.Priority,
                    LastObservedAt: (clock ?? SupervisionClock.System).Now));
            }
        }
        return sources;
    }

    public Uri BuildInstanceEndpoint(string application, string instance)
    {
        var relative = $"v2/servers/_defaultServer_/vhosts/_defaultVHost_/applications/{Uri.EscapeDataString(application)}/instances/{Uri.EscapeDataString(instance)}";
        return new Uri(server.ManagementBaseUri, relative);
    }

    private async Task<JsonDocument> GetInstanceAsync(string application, string instance, CredentialValue credential, CancellationToken cancellationToken)
    {
        var endpoint = BuildInstanceEndpoint(application, instance);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(server.ConnectionTimeout);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Error pages can echo credentials, application names or customer data.
            // Status is sufficient for diagnosis; never log an untrusted response body.
            throw new HttpRequestException($"Wowza inventory request returned HTTP {(int)response.StatusCode}; observation is unknown.", null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
    }
}

public sealed record WowzaIncomingStreamObservation(string StreamName, bool PublisherConnected, string? PublisherIp, long? UptimeSeconds);

public static class WowzaIncomingStreamParser
{
    public static IReadOnlyList<WowzaIncomingStreamObservation> Parse(JsonElement root)
    {
        var envelope = Properties(root);
        if (!envelope.TryGetValue("incomingStreams", out var array) || array.ValueKind != JsonValueKind.Array)
            throw Unknown();
        var result = new List<WowzaIncomingStreamObservation>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var properties = Properties(item);
            var name = Text(properties, "name");
            var alias = Text(properties, "streamName");
            if (name is not null && alias is not null && name != alias) throw Unknown();
            name ??= alias;
            if (string.IsNullOrWhiteSpace(name)) throw Unknown();
            // Match SourceIdentity normalization before detecting duplicates.
            name = name.Trim();
            if (!names.Add(name)) throw Unknown();
            bool? connected = null;
            foreach (var key in new[] { "isConnected", "isPublished", "state" })
            {
                if (!properties.TryGetValue(key, out var value)) continue;
                var flag = key == "state" ? State(value) : value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw Unknown()
                };
                if (connected is { } previous && previous != flag) throw Unknown();
                connected = flag;
            }
            if (connected is null) throw Unknown();
            result.Add(new(name, connected.Value, Text(properties, "sourceIp") ?? Text(properties, "sourceIPAddress"),
                Long(properties, "uptime") ?? Long(properties, "uptimeSeconds")));
        }
        return result;
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Unknown();
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value)) throw Unknown();
        return result;
    }

    private static string? Text(Dictionary<string, JsonElement> item, string name)
    {
        if (!item.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Unknown();
    }

    private static long? Long(Dictionary<string, JsonElement> item, string name)
    {
        if (!item.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var text)) return text;
        throw Unknown();
    }

    private static bool State(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()?.ToLowerInvariant() switch
        {
            "active" or "published" => true,
            "inactive" or "disconnected" or "unpublished" => false,
            _ => throw Unknown()
        } : throw Unknown();

    private static JsonException Unknown() => new("Wowza inventory schema is missing, invalid or ambiguous; observation is unknown.");
}
