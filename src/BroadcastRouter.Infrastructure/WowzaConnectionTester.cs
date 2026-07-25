using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record WowzaConnectionTestResult(
    bool Reachable,
    bool Authenticated,
    string Summary,
    string? DetectedVersion,
    int? HttpStatus);

public static class WowzaConnectionTester
{
    public static async Task<WowzaConnectionTestResult> TestAsync(WowzaServerProfile profile, string password, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(profile.ManagementUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            return new(false, false, "Management URL must be an absolute HTTP or HTTPS URL.", null, null);
        try { RtspUrlGenerator.ValidateTemplate(profile.RtspUrlTemplate); }
        catch (FormatException ex) { return new(false, false, $"RTSP template is invalid: {ex.Message}", null, null); }

        var handler = new HttpClientHandler();
        if (!profile.ValidateTlsCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.ConnectionTimeoutSeconds, 2, 60)) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(EnsureTrailingSlash(baseUri), "v2/servers"));
        if (!string.IsNullOrWhiteSpace(profile.Username))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Username}:{password}")));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return new(true, false, $"Server reached, but authentication failed (HTTP {status}).", null, status);
            if (!response.IsSuccessStatusCode)
                return new(true, false, $"Server reached but returned HTTP {status} {response.ReasonPhrase}.", null, status);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var version = FindVersion(body);
            return new(true, true, version is null ? "Connection and authentication succeeded." : $"Connection succeeded. Wowza version: {version}", version, status);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "Connection timed out.", null, null);
        }
        catch (HttpRequestException ex)
        {
            return new(false, false, $"Connection failed: {ex.Message}", null, ex.StatusCode is null ? null : (int)ex.StatusCode);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static string? FindVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindVersion(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static string? FindVersion(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains("version", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    return property.Value.ToString();
                var nested = FindVersion(property.Value);
                if (nested is not null) return nested;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) { var nested = FindVersion(child); if (nested is not null) return nested; }
        return null;
    }
}
