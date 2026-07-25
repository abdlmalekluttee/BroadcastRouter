using System.Text.RegularExpressions;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record RoutingRuleDecision(string PresetId, string? FixedPortId, string? PortGroup, bool Locked, int Priority, string? RuleId);

public static class RoutingRuleEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static RoutingRuleDecision Evaluate(DiscoveredSource source, IReadOnlyList<RoutingRuleProfile> rules, string defaultPresetId)
    {
        foreach (var rule in rules.Where(x => x.Enabled).OrderBy(x => x.Order))
        {
            if (!Match(rule.ServerPattern, source.Identity.ServerId) ||
                !Match(rule.ApplicationPattern, source.Identity.Application) ||
                !Match(rule.InstancePattern, source.Identity.ApplicationInstance) ||
                !Match(rule.StreamPattern, source.Identity.StreamName)) continue;
            if (!string.IsNullOrWhiteSpace(rule.Tag) && !(source.Tags?.Contains(rule.Tag) ?? false)) continue;
            if (!string.IsNullOrWhiteSpace(rule.Codec) && !string.Equals(rule.Codec, source.Media?.VideoCodec, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Width is not null && rule.Width != source.Media?.Width) continue;
            if (rule.Height is not null && rule.Height != source.Media?.Height) continue;
            if (rule.FramesPerSecond is not null && Math.Abs(rule.FramesPerSecond.Value - (source.Media?.FramesPerSecond ?? -100)) > 0.02) continue;
            if (rule.HasAudio is not null && rule.HasAudio != !string.IsNullOrWhiteSpace(source.Media?.AudioCodec)) continue;
            return new(rule.PresetId, EmptyToNull(rule.FixedPortId), EmptyToNull(rule.PortGroup), rule.LockAssignment,
                source.Priority + rule.PriorityAdjustment, rule.Id);
        }

        return new(defaultPresetId, source.FixedPortId, null, source.AssignmentLocked, source.Priority, null);
    }

    public static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return;
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            _ = new Regex(pattern[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static bool Match(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(value, pattern[6..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return Regex.IsMatch(value, $"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
