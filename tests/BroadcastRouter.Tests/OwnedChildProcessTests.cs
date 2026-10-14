using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BroadcastRouter.Infrastructure;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class OwnedChildProcessTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Tool => Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "BroadcastRouter.FakeFfmpeg",
        "bin", "Release", "net8.0", "BroadcastRouter.FakeFfmpeg.exe");
    private static ProcessStartInfo Start(int exit = 0) => new(Tool)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
        ArgumentList = { "--owned-child-contract", exit.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public async Task TokenizedArgumentsEnvironmentWorkingDirectoryAndStreamsSurviveFastExit(int exit)
    {
        var folder = Path.Combine(Path.GetTempPath(), "owned helper Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var start = Start(exit); start.WorkingDirectory = folder;
            string[] arguments = ["", "space value", "a\"b", "\\", "trailing \\", "\\\\\"quoted", "Ω漢字", "& | ; %PATH% $()"];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["BROADCASTROUTER_SYNTHETIC_VALUE"] = "one two Ω";
            using var child = OwnedChildProcess.Start(start);
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            await child.StandardInput.WriteLineAsync("stdin Ω"); child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(exit, child.ExitCode);
            using var json = JsonDocument.Parse(await stdout.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(arguments, json.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("stdin Ω", json.RootElement.GetProperty("Input").GetString());
            Assert.Equal("one two Ω", json.RootElement.GetProperty("EnvironmentValue").GetString());
            Assert.Equal(folder, json.RootElement.GetProperty("Directory").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("ConsoleWindow").GetInt64());
            Assert.Contains("synthetic stderr Ω", await stderr.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public async Task ParallelChildrenBelongToIndependentNonInheritableJobsAndPeerSurvivesDisposal()
    {
        var children = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => OwnedChildProcess.Start(Start()))));
        try
        {
            foreach (var child in children)
            {
                var job = (WindowsKillOnCloseJob)typeof(OwnedChildProcess).GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child)!;
                Assert.True(IsProcessInJob(child.SafeHandle.DangerousGetHandle(), job.NativeHandle, out var member));
                Assert.True(member);
                Assert.True(IsProcessInJob(Process.GetCurrentProcess().Handle, job.NativeHandle, out var hostMember));
                Assert.False(hostMember);
                Assert.True(GetHandleInformation(job.NativeHandle, out var flags));
                Assert.Equal(0u, flags & 1); // not inheritable; child cannot keep its own job alive
            }
            using var ended = Process.GetProcessById(children[0].Id); _ = ended.SafeHandle;
            children[0].Dispose();
            await ended.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(children.Skip(1), child => Assert.False(child.HasExited));
        }
        finally { foreach (var child in children) child.Dispose(); }
    }

    [Fact]
    public async Task ChildCannotUseAnUnrelatedInheritableHostHandle()
    {
        using var sentinel = new EventWaitHandle(false, EventResetMode.ManualReset);
        var handle = sentinel.SafeWaitHandle.DangerousGetHandle();
        Assert.True(SetHandleInformation(handle, 1, 1));
        try
        {
            var start = Start(); start.Environment["BROADCASTROUTER_SYNTHETIC_HANDLE"] = handle.ToInt64().ToString();
            using var child = OwnedChildProcess.Start(start);
            await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, child.ExitCode);
            Assert.False(sentinel.WaitOne(0), "Helper inherited an unrelated host handle outside its standard-handle allowlist.");
        }
        finally { Assert.True(SetHandleInformation(handle, 1, 0)); }
    }

    [Fact]
    public void AlternateCredentialsAreRefusedRatherThanSilentlyIgnored()
    {
        var start = Start(); start.UserName = "synthetic-other-account";
        Assert.Throws<InvalidOperationException>(() => { using var unexpected = OwnedChildProcess.Start(start); });
    }

    [Fact]
    public async Task ForcedTerminationPreservesTheExplicitProcessKillExitCode()
    {
        using var child = OwnedChildProcess.Start(Start());
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-1, child.ExitCode);
    }

    [Fact]
    public void InvalidExecutableAndNulArgumentDoNotLeakLaunchResources()
    {
        using var self = Process.GetCurrentProcess(); self.Refresh(); var before = self.HandleCount;
        for (var i = 0; i < 20; i++)
        {
            var missing = Start(); missing.FileName = Path.Combine(Path.GetTempPath(), "missing-owned-helper-" + Guid.NewGuid().ToString("N") + ".exe");
            Assert.Throws<System.ComponentModel.Win32Exception>(() => OwnedChildProcess.Start(missing));
            var invalid = Start(); invalid.ArgumentList.Add("bad\0argument");
            Assert.Throws<ArgumentException>(() => OwnedChildProcess.Start(invalid));
            self.Refresh(); output.WriteLine($"failed launch pair {i + 1}: handles={self.HandleCount}, initial={before}");
        }
        self.Refresh(); Assert.True(self.HandleCount - before < 16, "Failed launch leaked native handles.");
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
