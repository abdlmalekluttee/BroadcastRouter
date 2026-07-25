using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public static class RtspUrlGenerator
{
    private static readonly HashSet<string> AllowedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "{wowza-host}", "{rtsp-port}", "{application}", "{application-instance}", "{stream-name}", "{server-id}"
    };

    public static Uri Generate(WowzaServerConfiguration server, SourceIdentity source)
    {
        ValidateTemplate(server.RtspUrlTemplate);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{wowza-host}"] = EscapeHost(server.RtspHost),
            ["{rtsp-port}"] = server.RtspPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{application}"] = Uri.EscapeDataString(source.Application),
            ["{application-instance}"] = Uri.EscapeDataString(source.ApplicationInstance),
            ["{stream-name}"] = EscapePath(source.StreamName),
            ["{server-id}"] = Uri.EscapeDataString(source.ServerId)
        };

        var rendered = replacements.Aggregate(server.RtspUrlTemplate, (value, pair) => value.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase));
        if (!Uri.TryCreate(rendered, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The RTSP template did not produce an absolute rtsp:// URI.");
        return uri;
    }

    public static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 2048)
            throw new FormatException("RTSP template is empty or too long.");
        if (!template.Contains("{stream-name}", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("RTSP template must contain {stream-name}.");

        var opens = template.Count(c => c == '{');
        var closes = template.Count(c => c == '}');
        if (opens != closes) throw new FormatException("RTSP template contains unmatched braces.");

        var index = 0;
        while ((index = template.IndexOf('{', index)) >= 0)
        {
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new FormatException("RTSP template contains unmatched braces.");
            var token = template[index..(end + 1)];
            if (!AllowedTokens.Contains(token)) throw new FormatException($"Unsupported RTSP template token: {token}");
            index = end + 1;
        }
    }

    private static string EscapeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsControl) || host.Contains('/') || host.Contains('@'))
            throw new FormatException("Invalid RTSP host.");
        return host.Trim();
    }

    private static string EscapePath(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
}
