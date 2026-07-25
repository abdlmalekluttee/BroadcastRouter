using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class WowzaDiscoveryProvider(
    HttpClient httpClient,
    WowzaServerConfiguration server,
    ICredentialResolver credentialResolver) : IStreamDiscoveryProvider
{
    public async Task<IReadOnlyList<DiscoveredSource>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!server.Enabled) return [];
        var credential = await credentialResolver.ResolveAsync(server.CredentialReference, cancellationToken).ConfigureAwait(false);
        var sources = new List<DiscoveredSource>();
        foreach (var application in server.Applications)
        foreach (var instance in server.ApplicationInstances)
        {
            using var document = await GetInstanceAsync(application, instance, credential, cancellationToken).ConfigureAwait(false);
            foreach (var observation in WowzaIncomingStreamParser.Parse(document.RootElement))
            {
                if (!observation.PublisherConnected) continue;
                var identity = new SourceIdentity(server.ServerId, application, instance, observation.StreamName);
                sources.Add(new DiscoveredSource(
                    identity,
                    observation.StreamName,
                    RtspUrlGenerator.Generate(server, identity),
                    SourceState.PublisherActive,
                    server.Priority,
                    LastObservedAt: DateTimeOffset.UtcNow));
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
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var detail = body.Length > 300 ? body[..300] : body;
            throw new HttpRequestException($"Wowza GET {endpoint} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}", null, response.StatusCode);
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
        var array = FindArray(root, "incomingStreams") ?? FindArray(root, "incomingstreams");
        if (array is null) return [];
        var result = new List<WowzaIncomingStreamObservation>();
        foreach (var item in array.Value.EnumerateArray())
        {
            var name = Text(item, "name") ?? Text(item, "streamName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var connected = Boolean(item, "isConnected") ?? Boolean(item, "isPublished") ?? StateLooksActive(Text(item, "state"));
            result.Add(new(name, connected, Text(item, "sourceIp") ?? Text(item, "sourceIPAddress"), Long(item, "uptime") ?? Long(item, "uptimeSeconds")));
        }
        return result;
    }

    private static JsonElement? FindArray(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array) return property.Value.Clone();
                var nested = FindArray(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) { var nested = FindArray(child, name); if (nested is not null) return nested; }
        return null;
    }

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : null;
    private static long? Long(JsonElement item, string name) => item.TryGetProperty(name, out var value) && (value.TryGetInt64(out var result) || long.TryParse(value.ToString(), out result)) ? result : null;
    private static bool StateLooksActive(string? state) => state is not null && (state.Equals("active", StringComparison.OrdinalIgnoreCase) || state.Equals("published", StringComparison.OrdinalIgnoreCase));
}
