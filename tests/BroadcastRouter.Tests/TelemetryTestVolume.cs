using BroadcastRouter.Infrastructure;

namespace BroadcastRouter.Tests;

internal static class TelemetryTestVolume
{
    // Files remain real; unrelated storage/route tests must not depend on the
    // developer or hosted runner having 20 GB/20% free. Guard tests vary capacity.
    public static TelemetryVolumeSpace Read(string _) => new(100_000_000_000, 80_000_000_000);
}
