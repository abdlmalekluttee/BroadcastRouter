using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class BenchModeStartupTests
{
    [Theory]
    [InlineData("broadcast-router.db")]
    [InlineData("telemetry/segment.jsonl")]
    [InlineData("telemetry-directory")]
    public async Task LinkedWritableDataCannotPassIsolationPreflight(string descendant)
    {
        await using var fixture = await BenchReleasePreflightTests.Fixture.Create();
        var app = Path.Combine(Path.GetTempPath(), "bench-candidate-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(app, "data");
        Directory.CreateDirectory(data);
        try
        {
            string link;
            if (descendant == "telemetry-directory")
            {
                link = Path.Combine(data, "telemetry");
                Directory.CreateDirectory(link);
            }
            else
            {
                link = Path.Combine(data, descendant);
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                await File.WriteAllTextAsync(link, "Synthetic linked-file metadata fixture; not a real database.");
            }
            var file = Path.Combine(app, "bench-policy.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new BenchStartupPolicy(fixture.Root, fixture.Database,
                BenchOutputGuardTests.Bindings().ToArray())));
            var error = await Assert.ThrowsAsync<BenchSafetyException>(() => BenchModeStartup.LoadAndVerifyAsync(file, app, data,
                _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([]),
                // Simulate the OS metadata boundary; no symlink privilege or
                // production link is needed to prove the refusal path.
                path => File.GetAttributes(path) | (Path.GetFullPath(path).Equals(Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint : 0)));
            Assert.Contains("reparse-point", error.Message);
        }
        finally
        {
            Directory.Delete(app, true);
        }
    }

    [Fact]
    public async Task IsolatedReleasedCandidateReceivesItsImmutableGuardWithoutCreatingData()
    {
        await using var fixture = await BenchReleasePreflightTests.Fixture.Create();
        var app = Path.Combine(Path.GetTempPath(), "bench-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(app);
        try
        {
            var file = Path.Combine(app, "bench-policy.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new BenchStartupPolicy(fixture.Root, fixture.Database,
                BenchOutputGuardTests.Bindings().ToArray())));
            var guard = await BenchModeStartup.LoadAndVerifyAsync(file, app, Path.Combine(app, "data"),
                _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([]));
            Assert.Equal(5, guard.Bindings.Count);
            Assert.False(Directory.Exists(Path.Combine(app, "data")));
            BenchModeStartup.VerifySettings(new OperatorSettings());
        }
        finally { Directory.Delete(app, true); }
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("external-data")]
    [InlineData("enabled-output")]
    public async Task StartupRefusesUnsafeBenchBeforeCreatingDataOrStartingCoordinator(string defect)
    {
        await using var fixture = await BenchReleasePreflightTests.Fixture.Create();
        var app = Path.Combine(fixture.Root, "candidate");
        var data = Path.Combine(app, "data");
        Directory.CreateDirectory(app);
        var production = defect == "overlap" ? fixture.Root : Path.Combine(fixture.Root, "production");
        Directory.CreateDirectory(production);
        // The database fixture is already created; put its actual directory in the policy.
        // For the isolated case use a sibling candidate directory, not a child of production.
        if (defect != "overlap")
        {
            app = Path.Combine(Path.GetTempPath(), "bench-candidate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(app); data = Path.Combine(app, "data"); production = fixture.Root;
        }
        try
        {
            if (defect == "external-data") data = Path.Combine(fixture.Root, "unsafe-data");
            if (defect == "enabled-output")
            {
                fixture.Settings.DeckLinkPortOverrides[2].IsOutputPort = true;
                await fixture.Store.SaveSettingsAsync(fixture.Settings);
            }
            var file = Path.Combine(app, "bench-policy.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new BenchStartupPolicy(production, fixture.Database, BenchOutputGuardTests.Bindings().ToArray())));
            await Assert.ThrowsAsync<BenchSafetyException>(() => BenchModeStartup.LoadAndVerifyAsync(file, app, data,
                _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([])));
            Assert.False(Directory.Exists(data));
        }
        finally { if (!app.StartsWith(fixture.Root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(app, true); }
    }

    [Fact]
    public void BenchCannotBindANonLoopbackListener()
    {
        var settings = new OperatorSettings(); settings.Security.BindAddress = "0.0.0.0";
        Assert.Throws<BenchSafetyException>(() => BenchModeStartup.VerifySettings(settings));
    }

    [Fact]
    public async Task CoordinatorCannotDropGuardWhenSupervisorConfigurationChanges()
    {
        await using var fixture = await BenchReleasePreflightTests.Fixture.Create();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddHttpClient(); services.AddSignalR();
        services.AddSingleton(fixture.Store);
        services.AddSingleton(new BenchOutputGuard(BenchOutputGuardTests.Bindings()));
        services.AddSingleton<RouterCoordinator>();
        using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<RouterCoordinator>();
        var type = typeof(RouterCoordinator);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var ensure = type.GetMethod("EnsureSupervisor", flags)!;
        FfmpegProcessSupervisor? current = null;
        try
        {
            foreach (var executable in new[] { "synthetic-first.exe", "synthetic-next.exe" })
            {
                ensure.Invoke(coordinator, [executable, false]);
                current = (FfmpegProcessSupervisor)type.GetField("_supervisor", flags)!.GetValue(coordinator)!;
                await Assert.ThrowsAsync<BenchSafetyException>(() => current.StartPortStandbyAsync(new("TEST", "fixture", "default", "standby"),
                    BenchOutputGuardTests.Port(6), new("test", "Test", new(1920,1080,25,1,"uyvy422"), false, 0),
                    new(StandbyPattern.Black, null, "Synthetic", false), CancellationToken.None));
                Assert.Empty(current.Snapshot());
            }
        }
        finally { if (current is not null) await current.DisposeAsync(); }
    }
}
