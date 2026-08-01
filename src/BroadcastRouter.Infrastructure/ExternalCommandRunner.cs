using System.Diagnostics;

namespace BroadcastRouter.Infrastructure;

public sealed record ExternalCommandResult(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    string? StartError)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public static class ExternalCommandRunner
{
    public static async Task<ExternalCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool containOnWindows = false)
    {
        if (!File.Exists(executablePath)) return new(false, null, "", "", false, "Executable does not exist.");
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(false, null, "", "", false, "Process.Start returned no process.");
            using var containment = containOnWindows && OperatingSystem.IsWindows()
                ? WindowsKillOnCloseJob.Create()
                : null;
            if (containment is not null)
            {
                try { containment.Add(process); }
                catch
                {
                    await TerminateWithinAsync(process).ConfigureAwait(false);
                    throw;
                }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return new(true, process.ExitCode, await SafeAwait(stdoutTask).ConfigureAwait(false),
                    await SafeAwait(stderrTask).ConfigureAwait(false), false, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TerminateWithinAsync(process).ConfigureAwait(false);
                return new(true, SafeExitCode(process),
                    await SafeAwait(stdoutTask).ConfigureAwait(false), await SafeAwait(stderrTask).ConfigureAwait(false), true, null);
            }
            catch (OperationCanceledException)
            {
                await TerminateWithinAsync(process).ConfigureAwait(false);
                await SafeAwait(stdoutTask).ConfigureAwait(false);
                await SafeAwait(stderrTask).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, null, "", "", false, ex.Message);
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static async Task TerminateWithinAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        using var reapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(reapDeadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (reapDeadline.IsCancellationRequested) { }
        catch { }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }
}
