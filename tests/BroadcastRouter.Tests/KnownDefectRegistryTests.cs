using System.Reflection;
using System.Text.Json;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class KnownDefectRegistryTests
{
    [Fact]
    public void RegistryMatchesEveryTaggedCorrectnessTest()
    {
        var registry = JsonSerializer.Deserialize<RegistryEntry[]>(File.ReadAllText(
            Path.Combine(FaultHarnessTests.RepositoryRoot(), "tests", "KnownDefects.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var tests = typeof(KnownDefectTests).Assembly.GetTypes().SelectMany(type => type.GetMethods()
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => new { Type = type, Method = method }))
            .Where(test => Traits(test.Type).Concat(Traits(test.Method)).Contains(("Category", "KnownDefect"))).ToArray();
        Assert.Equal(registry.Length, tests.Length);
        Assert.Equal(registry.Length, registry.Select(r => r.Test).Distinct().Count());
        foreach (var test in tests)
        {
            var entry = Assert.Single(registry, r => r.Test == test.Type.FullName + "." + test.Method.Name);
            Assert.Contains(("Finding", entry.Finding), Traits(test.Method));
            Assert.StartsWith(entry.Finding + ": ", entry.Message);
        }
        Assert.Equal(136, LegacyRegressionTests.Cases.Count());
    }

    private static IEnumerable<(string, string)> Traits(MemberInfo member) => member.GetCustomAttributesData()
        .Where(a => a.AttributeType == typeof(TraitAttribute))
        .Select(a => ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!));
    private sealed record RegistryEntry(string Test, string Finding, string Message);
}
