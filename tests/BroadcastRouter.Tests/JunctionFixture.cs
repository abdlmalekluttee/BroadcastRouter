using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BroadcastRouter.Tests;

internal static class JunctionFixture
{
    public static void Create(string link, string target)
    {
        // Test-owned paths only. No shell/process startup and no privilege changes.
        var resolved = Path.GetFullPath(link);
        var destination = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(resolved)!;
        if (!OperatingSystem.IsWindows()
            || Path.GetDirectoryName(parent) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
            || !Path.GetFileName(parent).StartsWith("router-junction-", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(destination), parent, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(resolved) || File.Exists(resolved) || !Directory.Exists(destination))
            throw new InvalidOperationException("Junction creation is limited to a new sibling in the owned temporary fixture.");

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + destination);
        var print = Encoding.Unicode.GetBytes(destination);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            // REPARSE_DATA_BUFFER.MountPointReparseBuffer, offsets relative to PathBuffer.
            writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
            writer.Write(checked((ushort)(buffer.Length - 8)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)print.Length));
            writer.Write(substitute); writer.Write((ushort)0);
            writer.Write(print); writer.Write((ushort)0);
        }
        Directory.CreateDirectory(resolved);
        try
        {
            using var handle = CreateFileW(resolved, 0x40000000, 7, IntPtr.Zero, 3,
                0x02000000 | 0x00200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Directory.Delete(resolved); // Exact newly created link; never recurse into target.
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input,
        int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
}
