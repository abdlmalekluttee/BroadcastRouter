namespace BroadcastRouter.Domain;

public static class UniqueIdGenerator
{
    public static string Next(string prefix, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("An ID prefix is required.", nameof(prefix));
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{prefix}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException($"No available ID could be generated for prefix '{prefix}'.");
    }
}
