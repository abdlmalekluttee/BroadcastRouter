using System.Text.RegularExpressions;

namespace BroadcastRouter.Infrastructure;

public static partial class LogRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = AuthenticatedUri().Replace(value, match => $"{match.Groups["scheme"].Value}***:***@");
        redacted = Ipv4Address().Replace(redacted, "<redacted-ip>");
        redacted = BracketedIpv6Address().Replace(redacted, "[<redacted-ip>]");
        return redacted;
    }

    public static string RedactForDiagnostics(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = Redact(value);
        redacted = UriValue().Replace(redacted, match => $"{match.Groups["scheme"].Value}<redacted-uri>");
        redacted = Ipv4Address().Replace(redacted, "<redacted-ip>");
        return redacted;
    }

    [GeneratedRegex(@"(?<scheme>(?:https?|rtsps?)://)[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthenticatedUri();

    [GeneratedRegex(@"(?<scheme>(?:https?|rtsps?)://)[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UriValue();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Ipv4Address();

    [GeneratedRegex(@"\[[0-9a-f:]+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex BracketedIpv6Address();
}
