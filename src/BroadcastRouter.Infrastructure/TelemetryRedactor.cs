using System.Text.RegularExpressions;

namespace BroadcastRouter.Infrastructure;

/// <summary>Private operational log redaction; not the public diagnostic-export policy.</summary>
public static class TelemetryRedactor
{
    private static bool IsIpv6(string value) => System.Net.IPAddress.TryParse(value, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Discard an oversized line instead of truncating halfway through a secret.
        if (value.Length > 4096) return "[oversized diagnostic line omitted]";
        try
        {
            var safe = LogRedactor.RedactForDiagnostics(value);
            // Secret-bearing free text is not a reliable serialization format. Omit
            // the whole line rather than guess where quoted/escaped credentials end.
            if (Regex.IsMatch(safe, @"(?i)\b(?:password|passwd|pwd|token|secret|authorization|credential)[""']?\s*[:=]",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return "[secret-bearing diagnostic line omitted]";
            safe = Regex.Replace(safe, @"(?i)\bBearer\s+\S+", "Bearer [redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe, @"(?<![\w:])[a-fA-F0-9:]*:[a-fA-F0-9:.:%]*",
                match => IsIpv6(match.Value) || IsIpv6(match.Value.TrimEnd('.', ':')) ? "[redacted-ip]" : match.Value,
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            safe = Regex.Replace(safe, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n""<>]*", "[redacted-path]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return safe.Replace('\r', ' ').Replace('\n', ' ');
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic omitted: redaction timeout]"; }
    }
}
