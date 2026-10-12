namespace BroadcastRouter.Tests;

internal static class ProbeMarker
{
    public static int CountStarted(string path) => File.ReadAllLines(path).Length;
}
