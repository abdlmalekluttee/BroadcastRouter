using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BroadcastRouter.Infrastructure;

public static class WindowsDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var input = CreateBlob(bytes);
        try
        {
            if (!CryptProtectData(ref input, "BroadcastRouter credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect the credential.");
            try { return Convert.ToBase64String(CopyBlob(output)); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data); }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        var input = CreateBlob(Convert.FromBase64String(protectedValue));
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not unprotect the credential for this user.");
            try { return Encoding.UTF8.GetString(CopyBlob(output)); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally
        {
            if (description != IntPtr.Zero) LocalFree(description);
            if (input.Data != IntPtr.Zero) Marshal.FreeHGlobal(input.Data);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
