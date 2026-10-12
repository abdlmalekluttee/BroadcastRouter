using System.Text;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class ProbeMarkerTests
{
    [Fact]
    public void ProbeStartCanBeObservedWhileChildStillOwnsAppendHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), "probe-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer.Write(Encoding.UTF8.GetBytes("1234\n56"));
            writer.Flush();
            // Exactly one complete child marker; no exception and no claim based on a partial line.
            Assert.Equal(1, ProbeMarker.CountStarted(path));
            writer.Write(Encoding.UTF8.GetBytes("78\n"));
            writer.Flush();
            Assert.Equal(2, ProbeMarker.CountStarted(path));
        }
        finally { File.Delete(path); }
    }
}
