namespace BroadcastRouter.Tests;

internal static class FixtureDirectory
{
    public static async Task DeleteAsync(string path, TimeSpan? timeout = null)
    {
        await Task.CompletedTask;
        Directory.Delete(path, true);
    }
}
