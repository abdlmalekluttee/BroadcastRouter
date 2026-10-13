namespace BroadcastRouter.Tests;

internal static class ProbeMarker
{
    public static int CountStarted(string path)
    {
        var published = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.started").Length;
        if (!File.Exists(path)) return published;
        // The fake child may still be appending. Do not deny its write handle,
        // and do not treat a partially written PID as a completed start marker.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return published + reader.ReadToEnd().Count(character => character == '\n');
    }
}
