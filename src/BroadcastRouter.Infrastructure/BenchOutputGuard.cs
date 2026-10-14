using System.Diagnostics;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record BenchPortBinding(int Connector, string StableId, string DeviceHandle, string DeviceGroupId, int SubdeviceIndex);

public sealed class BenchSafetyException(string message) : InvalidOperationException(message);

public sealed class BenchOutputGuard
{
    private readonly IReadOnlyList<BenchPortBinding> _bindings;
    public IReadOnlyList<BenchPortBinding> Bindings => _bindings;

    public BenchOutputGuard(IEnumerable<BenchPortBinding> bindings)
    {
        var copy = bindings.ToArray();
        if (copy.Length != 5 || !copy.Select(p => p.Connector).Order().SequenceEqual(new[] { 1, 2, 3, 4, 5 })
            || copy.Any(p => string.IsNullOrWhiteSpace(p.StableId) || string.IsNullOrWhiteSpace(p.DeviceHandle)
                || string.IsNullOrWhiteSpace(p.DeviceGroupId) || p.SubdeviceIndex != p.Connector - 1)
            || copy.Select(p => p.StableId).Distinct(StringComparer.Ordinal).Count() != 5
            || copy.Select(p => p.DeviceHandle).Distinct(StringComparer.Ordinal).Count() != 5
            || copy.Select(p => p.DeviceGroupId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new BenchSafetyException("Bench policy refused: five unique, exact connectors on one card are required.");
        _bindings = Array.AsReadOnly(copy);
    }

    internal void ValidateIdentity(DeckLinkPort? port)
    {
        var binding = _bindings.SingleOrDefault(p => p.StableId == port?.StableId);
        if (port is null || binding is null || string.IsNullOrWhiteSpace(port.PersistentId)
            || !port.PersistentId.StartsWith("0x", StringComparison.Ordinal)
            || port.StableId != "DECKLINK-PERSISTENT-" + port.PersistentId[2..]
            || port.DeviceHandle != binding.DeviceHandle || port.FfmpegName != binding.DeviceHandle
            || port.DeviceGroupId != binding.DeviceGroupId || port.SubdeviceIndex != binding.SubdeviceIndex)
            throw new BenchSafetyException("Bench output refused: physical identity is not an exact allowlisted target.");
    }

    public void ValidateLaunch(DeckLinkPort? port, ProcessStartInfo start)
    {
        ValidateIdentity(port);
        var args = start.ArgumentList;
        var decklinkOutputs = Enumerable.Range(0, Math.Max(0, args.Count - 1))
            .Count(i => args[i] == "-f" && args[i + 1] == "decklink");
        if (!port!.IsAvailable || start.UseShellExecute || !string.IsNullOrEmpty(start.Arguments)
            || args.Count < 3 || decklinkOutputs != 1 || args[^1] != port.DeviceHandle)
            throw new BenchSafetyException("Bench output refused: command does not target exactly one allowlisted device handle.");
    }
}
