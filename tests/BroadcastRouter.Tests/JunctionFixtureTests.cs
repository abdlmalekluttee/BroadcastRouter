using Xunit;

namespace BroadcastRouter.Tests;

public sealed class JunctionFixtureTests
{
    [Fact]
    public void JunctionFixtureReachesTargetAndUnlinkPreservesTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "router-junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel.txt"), "keep target");
        try
        {
            JunctionFixture.Create(link, target);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal("keep target", File.ReadAllText(Path.Combine(link, "sentinel.txt")));
            Directory.Delete(link); // Unlink only; never follow the fixture junction.
            Assert.True(File.Exists(Path.Combine(target, "sentinel.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }
}
