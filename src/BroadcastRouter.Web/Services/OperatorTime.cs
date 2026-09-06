namespace BroadcastRouter.Web.Services;

public static class OperatorTime
{
    public static string Relative(DateTimeOffset? timestamp, DateTimeOffset? now = null)
    {
        if (timestamp is null) return "Never";
        var age = (now ?? DateTimeOffset.UtcNow) - timestamp.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromSeconds(2)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
