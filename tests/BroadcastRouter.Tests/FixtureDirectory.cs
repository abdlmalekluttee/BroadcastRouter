namespace BroadcastRouter.Tests;

internal static class FixtureDirectory
{
    public static async Task DeleteAsync(string path, TimeSpan? timeout = null)
    {
        var resolved = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(resolved);
        if (!string.Equals(parent?.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("router-recovery-", StringComparison.Ordinal))
            throw new InvalidOperationException("Cleanup is restricted to the owned recovery fixture directory.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)))
            { await Task.Delay(50); }
        }
    }
}
