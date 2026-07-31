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
        CancellationToken cancellationToken)
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
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return new(true, process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), false, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                return new(true, process.HasExited ? process.ExitCode : null,
                    await SafeAwait(stdoutTask).ConfigureAwait(false), await SafeAwait(stderrTask).ConfigureAwait(false), true, null);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, null, "", "", false, ex.Message);
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
    }
}
