using System.Net;

namespace BroadcastRouter.Infrastructure;

public static class NetworkAccessPolicy
{
    public static void Validate(string configured)
    {
        foreach (var token in Tokens(configured))
            _ = Parse(token);
    }

    public static bool IsAllowed(IPAddress address, string configured)
    {
        var candidate = Normalize(address);
        foreach (var token in Tokens(configured))
        {
            var (network, prefix) = Parse(token);
            var normalizedNetwork = Normalize(network);
            var left = candidate.GetAddressBytes();
            var right = normalizedNetwork.GetAddressBytes();
            if (left.Length != right.Length) continue;

            var fullBytes = prefix / 8;
            var remainingBits = prefix % 8;
            if (!left.AsSpan(0, fullBytes).SequenceEqual(right.AsSpan(0, fullBytes))) continue;
            if (remainingBits == 0) return true;
            var mask = (byte)(0xff << (8 - remainingBits));
            if ((left[fullBytes] & mask) == (right[fullBytes] & mask)) return true;
        }
        return false;
    }

    public static void ValidateExposure(string bindAddress, bool requireAuthentication)
    {
        if (!IPAddress.TryParse(bindAddress, out var address))
            throw new InvalidOperationException("Bind address must be a valid IP address.");
        if (!requireAuthentication && !IPAddress.IsLoopback(Normalize(address)))
            throw new InvalidOperationException("Authentication is required when binding BroadcastRouter to a non-loopback address.");
    }

    public static IReadOnlyList<IPAddress> ParseTrustedProxies(string configured)
    {
        var result = new List<IPAddress>();
        foreach (var token in Tokens(configured))
        {
            if (token.Contains('/') || !IPAddress.TryParse(token, out var address))
                throw new InvalidOperationException($"Trusted proxy '{token}' must be an exact IP address, not a CIDR range.");
            result.Add(Normalize(address));
        }
        return result;
    }

    public static bool IsClientAllowed(IPAddress rawPeer, IPAddress effectiveClient, string configured)
    {
        rawPeer = Normalize(rawPeer);
        effectiveClient = Normalize(effectiveClient);
        var directLoopback = IPAddress.IsLoopback(rawPeer) && rawPeer.Equals(effectiveClient);
        if (IPAddress.IsLoopback(effectiveClient)) return directLoopback;
        return IsAllowed(effectiveClient, configured);
    }

    private static IEnumerable<string> Tokens(string configured) =>
        configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (IPAddress Network, int Prefix) Parse(string token)
    {
        var parts = token.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var network))
            throw new InvalidOperationException($"Allowed network '{token}' is not a valid IP address or CIDR range.");

        network = Normalize(network);
        var maximum = network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maximum;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maximum))
            throw new InvalidOperationException($"Allowed network '{token}' has an invalid CIDR prefix.");
        return (network, prefix);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
