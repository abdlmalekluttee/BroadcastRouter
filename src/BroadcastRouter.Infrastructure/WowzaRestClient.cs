using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class WowzaRestClient(HttpClient httpClient)
{
    public async Task<JsonDocument> GetApplicationsAsync(WowzaServerConfiguration server, string username, string password, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(server.ManagementBaseUri, "v2/servers/_defaultServer_/vhosts/_defaultVHost_/applications");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
