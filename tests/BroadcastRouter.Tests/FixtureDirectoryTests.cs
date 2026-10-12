using Xunit;

namespace BroadcastRouter.Tests;

public sealed class FixtureDirectoryTests
{
    [Fact]
    public async Task CleanupWaitsForTransientWindowsFileLockWithoutIgnoringFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "router-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = new FileStream(Path.Combine(root, "synthetic.dll"), FileMode.Create, FileAccess.Write, FileShare.Read);
        Task? cleanup = null;
        try
        {
            cleanup = FixtureDirectory.DeleteAsync(root, TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            Assert.False(cleanup.IsCompleted, "Fixture cleanup must tolerate a short mapped-file/AV lock, not fail immediately.");
            file.Dispose();
            await cleanup;
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            file.Dispose();
            if (cleanup is not null) { try { await cleanup; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PersistentLockStillFailsWithinBoundedCleanupDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "router-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var file = new FileStream(Path.Combine(root, "synthetic.dll"), FileMode.Create, FileAccess.Write, FileShare.Read);
            var failure = await Record.ExceptionAsync(() => FixtureDirectory.DeleteAsync(root, TimeSpan.FromMilliseconds(200)));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.True(File.Exists(Path.Combine(root, "synthetic.dll")));
        }
        finally { Directory.Delete(root, true); }
    }
}
