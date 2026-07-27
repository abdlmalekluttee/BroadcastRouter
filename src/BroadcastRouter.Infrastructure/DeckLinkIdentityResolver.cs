using System.Security.Cryptography;
using System.Text;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public static class DeckLinkIdentityResolver
{
    public static IReadOnlyList<DeckLinkPort> Resolve(
        IReadOnlyList<DeckLinkSink> sinks,
        IReadOnlyList<DeckLinkHardwareIdentity> hardware)
    {
        var byHandle = hardware
            .Where(identity => !string.IsNullOrWhiteSpace(identity.DeviceHandle))
            .GroupBy(identity => identity.DeviceHandle, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var persistentCounts = hardware
            .Where(identity => identity.PersistentId.HasValue)
            .GroupBy(identity => identity.PersistentId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var groupOrder = hardware
            .Where(identity => identity.DeviceGroupId.HasValue)
            .Select(identity => identity.DeviceGroupId!.Value)
            .Distinct()
            .OrderBy(value => value)
            .Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index);

        return sinks.Select((sink, index) =>
        {
            var legacyId = LegacyStableId(sink.FfmpegAddress);
            byHandle.TryGetValue(sink.FfmpegAddress, out var identity);
            var persistent = identity?.PersistentId;
            var persistentIsUnique = persistent.HasValue
                && persistentCounts.TryGetValue(persistent.Value, out var count)
                && count == 1;
            if (!persistentIsUnique)
            {
                return new DeckLinkPort(
                    legacyId, sink.FfmpegAddress, identity?.ModelName ?? sink.DisplayName,
                    index, identity?.SubdeviceIndex ?? 0, null, [], true,
                    identity?.DisplayName ?? sink.DisplayName,
                    identity is null
                        ? "FFmpeg device handle only — verify after driver or topology changes"
                        : "DeckLink SDK did not expose a unique persistent ID — verify after topology changes",
                    DeviceHandle: identity?.DeviceHandle,
                    DeviceGroupId: Format(identity?.DeviceGroupId),
                    TopologicalId: Format(identity?.TopologicalId));
            }

            var cardIndex = identity!.DeviceGroupId is uint groupId && groupOrder.TryGetValue(groupId, out var groupIndex)
                ? groupIndex
                : index;
            return new DeckLinkPort(
                StableId: $"DECKLINK-PERSISTENT-{persistent!.Value:X8}",
                FfmpegName: sink.FfmpegAddress,
                ModelName: identity.ModelName,
                CardIndex: cardIndex,
                SubdeviceIndex: identity.SubdeviceIndex ?? 0,
                PciLocation: null,
                SupportedModes: [],
                IsAvailable: true,
                FriendlyName: identity.DisplayName,
                IdentityConfidence: "Blackmagic persistent hardware ID — stable across PCIe slots and reboots",
                PersistentId: Format(identity.PersistentId),
                DeviceGroupId: Format(identity.DeviceGroupId),
                DeviceHandle: identity.DeviceHandle,
                TopologicalId: Format(identity.TopologicalId),
                PreviousStableIds: [legacyId]);
        }).ToArray();
    }

    public static string LegacyStableId(string ffmpegAddress) =>
        $"FFMPEG-NAME-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ffmpegAddress)))[..16]}";

    private static string? Format(uint? value) => value.HasValue ? $"0x{value.Value:X8}" : null;
}
