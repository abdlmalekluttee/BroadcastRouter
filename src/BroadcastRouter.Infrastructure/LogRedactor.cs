using System.Text.RegularExpressions;

namespace BroadcastRouter.Infrastructure;

public static partial class LogRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return AuthenticatedUri().Replace(value, match => $"{match.Groups["scheme"].Value}***:***@");
    }

    [GeneratedRegex(@"(?<scheme>rtsps?://)[^\s/@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthenticatedUri();
}
