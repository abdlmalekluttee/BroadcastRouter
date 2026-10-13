using System.Security.Cryptography;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class BenchReleasePreflightTests
{
    [Theory]
    [InlineData("enabled")]
    [InlineData("missing-selection")]
    [InlineData("missing-identity")]
    [InlineData("changed-handle")]
    [InlineData("saved-route")]
    [InlineData("running-route")]
    [InlineData("lingering-owner")]
    [InlineData("unreadable-owner")]
    [InlineData("name-alias-owner")]
    [InlineData("bad-json")]
    [InlineData("missing-db")]
    public async Task RefusesStartupUnlessAllFiveProductionOutputsAreProvablyReleased(string defect)
    {
        await using var fixture = await Fixture.Create();
        IReadOnlyList<BenchProcessEvidence> processes = [];
        if (defect == "enabled") fixture.Settings.DeckLinkPortOverrides[4].IsOutputPort = true;
        if (defect == "missing-selection") fixture.Settings.DeckLinkPortOverrides.RemoveAt(4);
        if (defect is "enabled" or "missing-selection") await fixture.Store.SaveSettingsAsync(fixture.Settings);
        if (defect == "missing-identity") await fixture.Sql("DELETE FROM ports WHERE port_id='DECKLINK-PERSISTENT-00000105'");
        if (defect == "changed-handle") await fixture.Store.UpsertPortAsync(BenchOutputGuardTests.Port(5) with { DeviceHandle = "OTHER-HANDLE" });
        if (defect is "saved-route" or "running-route")
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            await fixture.Store.SaveRouteAsync(new("TEST/fixture/default/source", "Synthetic",
                defect == "running-route" ? "DECKLINK-PERSISTENT-00000105" : null, "Synthetic output", "1080i50",
                defect == "running-route" ? RouteState.Running : RouteState.WaitingForStream, AssignmentMode.Manual,
                false, 0, 0, null, null, null, 0, 0, null, now, null, null,
                DesiredPortId: "DECKLINK-PERSISTENT-00000105"), RouteState.WaitingForStream);
        }
        if (defect == "lingering-owner") processes = [new(123, "renamed-media.exe", "renamed-media.exe -f decklink TEST-HANDLE-5")];
        if (defect == "unreadable-owner") processes = [new(123, "ffmpeg.exe", null)];
        if (defect == "name-alias-owner") processes = [new(123, "ffmpeg.exe", "ffmpeg.exe -f decklink \"Synthetic identical model (5)\"")];
        if (defect == "bad-json") await fixture.Sql("UPDATE settings SET json='{}' WHERE profile='active'");
        var database = defect == "missing-db" ? Path.Combine(fixture.Root, "absent.db") : fixture.Database;
        var error = await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(database,
            new(BenchOutputGuardTests.Bindings()), _ => Task.FromResult(processes)));
        Assert.Contains("Bench preflight refused", error.Message);
        if (defect == "missing-db") Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task FreshReadOnlyPreflightAcceptsReleasedPortsWithoutChangingStoredData()
    {
        await using var fixture = await Fixture.Create();
        // A protected sixth connector's real handle is not a collision.
        await fixture.Store.UpsertPortAsync(BenchOutputGuardTests.Port(6));
        await fixture.Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        var before = HashDatabase(fixture.Database);
        await BenchReleasePreflight.EnsureReleasedAsync(fixture.Database, new(BenchOutputGuardTests.Bindings()),
            _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([new(456, "ffmpeg.exe", "ffmpeg.exe -f decklink TEST-HANDLE-6")]));
        Assert.Equal(before, HashDatabase(fixture.Database));
        Assert.Equal(fixture.Settings.ConfigurationRevision, (await fixture.Store.LoadSettingsAsync()).ConfigurationRevision);
    }

    private static byte[] HashDatabase(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    [Fact]
    public async Task RefusesReadFailureRatherThanTreatingItAsNoOwners()
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(fixture.Database,
            new(BenchOutputGuardTests.Bindings()), _ => throw new IOException("synthetic inaccessible process inventory")));
    }

    [Fact]
    public async Task RechecksConfigurationChangedWhileProcessInventoryWasRead()
    {
        await using var fixture = await Fixture.Create();
        var error = await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(fixture.Database,
            new(BenchOutputGuardTests.Bindings()), async _ =>
            {
                fixture.Settings.DeckLinkPortOverrides[0].IsOutputPort = true;
                await fixture.Store.SaveSettingsAsync(fixture.Settings);
                return Array.Empty<BenchProcessEvidence>();
            }));
        Assert.Contains("output selections", error.Message);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bench-preflight-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, "production-fixture.db");
        internal SqliteDataStore Store { get; }
        internal OperatorSettings Settings { get; } = new();
        private Fixture() { Directory.CreateDirectory(Root); Store = new(Database); }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Store.InitializeAsync();
            foreach (var n in Enumerable.Range(1, 5))
            {
                var port = BenchOutputGuardTests.Port(n) with { IsOutputPort = false };
                await fixture.Store.UpsertPortAsync(port);
                fixture.Settings.DeckLinkPortOverrides.Add(new() { StableId = port.StableId, IsOutputPort = false,
                    StandbyPresetId = fixture.Settings.Presets[0].Id });
            }
            await fixture.Store.SaveSettingsAsync(fixture.Settings);
            return fixture;
        }
        internal async Task Sql(string text)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = text;
            await command.ExecuteNonQueryAsync();
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
