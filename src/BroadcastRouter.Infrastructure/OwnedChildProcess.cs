using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BroadcastRouter.Infrastructure;

/// <summary>
/// A helper is a job member before it can execute. Only its three standard pipe
/// handles may be inherited. No shell, breakaway, PID-only kill or Process-field
/// injection. The exact process handle is acquired while the child is suspended.
/// </summary>
internal sealed class OwnedChildProcess : IDisposable
{
    private Process? _process;
    private WindowsKillOnCloseJob? _job;
    private StreamWriter? _input;
    private StreamReader? _output, _error;
    private bool _enableEvents;
    public ProcessStartInfo StartInfo { get; set; } = new();
    public bool EnableRaisingEvents { get => _enableEvents; set => _enableEvents = value; }
    private Process Process => _process ?? throw new InvalidOperationException("Helper has not started.");
    public int Id => Process.Id;
    public DateTime StartTime => Process.StartTime;
    public bool HasExited => Process.HasExited;
    public int ExitCode => Process.ExitCode;
    public SafeProcessHandle SafeHandle => Process.SafeHandle;
    public StreamWriter StandardInput => _input ?? Process.StandardInput;
    public StreamReader StandardOutput => _output ?? Process.StandardOutput;
    public StreamReader StandardError => _error ?? Process.StandardError;

    public static OwnedChildProcess Start(ProcessStartInfo start)
    {
        var process = new OwnedChildProcess { StartInfo = start };
        try { process.Start(); return process; }
        catch { process.Dispose(); throw; }
    }

    public bool Start()
    {
        if (_process is not null) throw new InvalidOperationException("Helper already started.");
        if (!OperatingSystem.IsWindows())
        {
            _process = System.Diagnostics.Process.Start(StartInfo) ?? throw new InvalidOperationException("Helper did not start.");
            return true;
        }
        if (StartInfo.UseShellExecute || !string.IsNullOrEmpty(StartInfo.Arguments)
            || !string.IsNullOrEmpty(StartInfo.UserName) || !string.IsNullOrEmpty(StartInfo.Domain)
            || StartInfo.Password is not null || StartInfo.PasswordInClearText is not null
            || !StartInfo.RedirectStandardOutput || !StartInfo.RedirectStandardError)
            throw new InvalidOperationException("Owned helpers require tokenized direct execution and redirected output.");

        _job = WindowsKillOnCloseJob.Create();
        AnonymousPipeServerStream? input = null, output = null, error = null;
        var attributes = IntPtr.Zero; var jobValue = IntPtr.Zero; var handles = IntPtr.Zero; var environment = IntPtr.Zero;
        var information = new Native.ProcessInformation();
        var attributeInitialized = false;
        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            _input = new StreamWriter(input, StartInfo.StandardInputEncoding ?? Console.InputEncoding) { AutoFlush = true };
            _output = new StreamReader(output, StartInfo.StandardOutputEncoding ?? Console.OutputEncoding);
            _error = new StreamReader(error, StartInfo.StandardErrorEncoding ?? Console.OutputEncoding);
            var standardHandles = new[] { input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) ThrowNative("Could not initialize helper attributes.");
            attributeInitialized = true;
            jobValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobValue, _job.NativeHandle);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x0002000D, jobValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowNative("Could not set atomic helper containment.");
            handles = Marshal.AllocHGlobal(IntPtr.Size * standardHandles.Length);
            Marshal.Copy(standardHandles, 0, handles, standardHandles.Length);
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020002, handles,
                (IntPtr)(IntPtr.Size * standardHandles.Length), IntPtr.Zero, IntPtr.Zero)) ThrowNative("Could not restrict helper handles.");
            var startup = new Native.StartupInfoEx
            {
                Startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                    Input = standardHandles[0], Output = standardHandles[1], Error = standardHandles[2] },
                Attributes = attributes
            };
            var command = new StringBuilder(Quote(StartInfo.FileName));
            foreach (var argument in StartInfo.ArgumentList) command.Append(' ').Append(Quote(argument));
            var env = string.Join('\0', StartInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)) + "\0";
            environment = Marshal.StringToHGlobalUni(env);
            // Match Process.Start's search semantics for an executable on PATH.
            // The first token is always quoted; this is direct CreateProcess,
            // not shell parsing or concatenation of a raw command string.
            if (!Native.CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x08000000 | 0x00000400 | 0x00000004, environment,
                string.IsNullOrWhiteSpace(StartInfo.WorkingDirectory) ? null : StartInfo.WorkingDirectory,
                ref startup, out information)) ThrowNative("Owned helper launch failed.");
            _process = System.Diagnostics.Process.GetProcessById(information.ProcessId);
            _ = _process.SafeHandle; // acquire before ResumeThread: fast exit cannot race PID lookup
            _process.EnableRaisingEvents = _enableEvents;
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle();
            if (!StartInfo.RedirectStandardInput) { _input.Dispose(); _input = null; }
            if (Native.ResumeThread(information.Thread) == uint.MaxValue) ThrowNative("Could not resume contained helper.");
            return true;
        }
        catch
        {
            _job.Dispose(); // also owns any successfully-created suspended process
            _input?.Dispose(); _output?.Dispose(); _error?.Dispose();
            input?.Dispose(); output?.Dispose(); error?.Dispose();
            _process?.Dispose(); _process = null;
            throw;
        }
        finally
        {
            // AnonymousPipeServerStream does not close the local client copy on
            // Dispose. Failed CreateProcess/validation must close all three too.
            input?.DisposeLocalCopyOfClientHandle(); output?.DisposeLocalCopyOfClientHandle(); error?.DisposeLocalCopyOfClientHandle();
            if (information.Thread != IntPtr.Zero) Native.CloseHandle(information.Thread);
            if (information.Process != IntPtr.Zero) Native.CloseHandle(information.Process);
            if (attributeInitialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobValue != IntPtr.Zero) Marshal.FreeHGlobal(jobValue);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _job?.Dispose(); // descendants cannot retain pipe writers after parent exit
    }
    public void WaitForExit() { Process.WaitForExit(); _job?.Dispose(); }
    public void Kill(bool entireProcessTree = false)
    {
        if (entireProcessTree && _job is not null) _job.Terminate();
        else Process.Kill(entireProcessTree);
    }
    public void Dispose()
    {
        _job?.Dispose(); _input?.Dispose(); _output?.Dispose(); _error?.Dispose(); _process?.Dispose();
    }

    // Windows CRT argv quoting. Always quote, double backslashes before a quote
    // or closing quote, and preserve all other characters (no shell expansion).
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Helper argument contains a NUL character.");
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void ThrowNative(string message) => throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size; internal IntPtr Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
            internal short ShowWindow, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory,
            ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
