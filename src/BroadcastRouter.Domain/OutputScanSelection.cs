namespace BroadcastRouter.Domain;

public static class OutputScanSelection
{
    public const string Progressive = "progressive";
    public const string Interlaced = "interlaced";

    public static string Format(bool interlaced) => interlaced ? Interlaced : Progressive;

    public static bool TryParse(string? value, out bool interlaced)
    {
        if (string.Equals(value, Interlaced, StringComparison.Ordinal))
        {
            interlaced = true;
            return true;
        }

        if (string.Equals(value, Progressive, StringComparison.Ordinal))
        {
            interlaced = false;
            return true;
        }

        interlaced = false;
        return false;
    }
}
