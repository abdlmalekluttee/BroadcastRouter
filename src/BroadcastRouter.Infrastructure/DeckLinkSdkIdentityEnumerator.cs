using System.Runtime.InteropServices;

namespace BroadcastRouter.Infrastructure;

public sealed record DeckLinkHardwareIdentity(
    string DeviceHandle,
    string DisplayName,
    string ModelName,
    uint? PersistentId,
    uint? DeviceGroupId,
    uint? TopologicalId,
    int? SubdeviceIndex,
    bool? HasReferenceInput = null,
    bool? ReferenceSignalLocked = null);

public static class DeckLinkSdkIdentityEnumerator
{
    private static readonly Guid IteratorClassId = new("BA6C6F44-6DA5-4DCE-94AA-EE2D1372A676");
    private const uint PersistentId = 0x70656964; // peid
    private const uint DeviceGroupId = 0x64676964; // dgid
    private const uint TopologicalId = 0x746F6964; // toid
    private const uint SubdeviceIndex = 0x73756269; // subi
    private const uint DeviceHandle = 0x64657668; // devh
    private const uint HasReferenceInput = 0x6872696E; // hrin
    private const uint ReferenceSignalLocked = 0x7265666C; // refl

    public static IReadOnlyList<DeckLinkHardwareIdentity> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return [];
        object? iteratorObject = null;
        try
        {
            var iteratorType = Type.GetTypeFromCLSID(IteratorClassId, throwOnError: false);
            if (iteratorType is null) return [];
            iteratorObject = Activator.CreateInstance(iteratorType);
            if (iteratorObject is not IDeckLinkIterator iterator) return [];

            var devices = new List<DeckLinkHardwareIdentity>();
            while (iterator.Next(out var device) == 0 && device is not null)
            {
                try
                {
                    _ = device.GetModelName(out var modelName);
                    _ = device.GetDisplayName(out var displayName);
                    if (device is not IDeckLinkProfileAttributes attributes) continue;
                    if (attributes.GetString(DeviceHandle, out var handle) != 0 || string.IsNullOrWhiteSpace(handle)) continue;
                    var hasReferenceInput = TryGetFlag(attributes, HasReferenceInput);
                    bool? referenceSignalLocked = null;
                    if (hasReferenceInput == true && device is IDeckLinkStatus status
                        && status.GetFlag(ReferenceSignalLocked, out var locked) == 0)
                        referenceSignalLocked = locked != 0;
                    devices.Add(new(
                        handle.Trim(),
                        displayName?.Trim() ?? handle.Trim(),
                        modelName?.Trim() ?? displayName?.Trim() ?? "DeckLink",
                        TryGetUInt32(attributes, PersistentId),
                        TryGetUInt32(attributes, DeviceGroupId),
                        TryGetUInt32(attributes, TopologicalId),
                        TryGetIndex(attributes, SubdeviceIndex),
                        hasReferenceInput,
                        referenceSignalLocked));
                }
                finally { Marshal.FinalReleaseComObject(device); }
            }
            return devices;
        }
        catch (COMException) { return []; }
        catch (InvalidCastException) { return []; }
        catch (PlatformNotSupportedException) { return []; }
        finally
        {
            if (iteratorObject is not null && Marshal.IsComObject(iteratorObject))
                Marshal.FinalReleaseComObject(iteratorObject);
        }
    }

    private static uint? TryGetUInt32(IDeckLinkProfileAttributes attributes, uint id) =>
        attributes.GetInt(id, out var value) == 0 && value is >= uint.MinValue and <= uint.MaxValue
            ? (uint)value
            : null;

    private static int? TryGetIndex(IDeckLinkProfileAttributes attributes, uint id) =>
        attributes.GetInt(id, out var value) == 0 && value is >= 0 and <= int.MaxValue
            ? (int)value
            : null;

    private static bool? TryGetFlag(IDeckLinkProfileAttributes attributes, uint id) =>
        attributes.GetFlag(id, out var value) == 0 ? value != 0 : null;

    [ComImport, Guid("50FB36CD-3063-4B73-BDBB-958087F2D8BA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeckLinkIterator
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out IDeckLink? deckLinkInstance);
    }

    [ComImport, Guid("C418FBDD-0587-48ED-8FE5-640F0A14AF91"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeckLink
    {
        [PreserveSig]
        int GetModelName([MarshalAs(UnmanagedType.BStr)] out string? modelName);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.BStr)] out string? displayName);
    }

    [ComImport, Guid("F47551D7-AD22-47AF-BCFD-6BE88AA879D9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeckLinkProfileAttributes
    {
        [PreserveSig] int GetFlag(uint cfgId, out int value);
        [PreserveSig] int GetInt(uint cfgId, out long value);
        [PreserveSig] int GetFloat(uint cfgId, out double value);
        [PreserveSig] int GetString(uint cfgId, [MarshalAs(UnmanagedType.BStr)] out string? value);
        [PreserveSig] int GetStringWithParam(uint cfgId, ulong parameter, [MarshalAs(UnmanagedType.BStr)] out string? value);
    }

    [ComImport, Guid("2A04A635-ED42-41EF-9342-0E11F8CF6B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeckLinkStatus
    {
        [PreserveSig] int GetFlag(uint statusId, out int value);
    }
}
