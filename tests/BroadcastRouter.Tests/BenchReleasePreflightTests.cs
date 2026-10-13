using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class BenchReleasePreflightTests
{
    [Fact]
    public async Task EnabledLegacySelectionCannotHideBehindDisabledCanonicalSelection()
    {
        await using var fixture = await Fixture.Create();
        var canonical = BenchOutputGuardTests.Port(5) with
        { IsOutputPort = false, PreviousStableIds = [DeckLinkIdentityResolver.LegacyStableId("TEST-HANDLE-5")] };
        await fixture.Store.UpsertPortAsync(canonical);
        await fixture.Store.UpsertPortAsync(canonical with { StableId = canonical.PreviousStableIds[0], PreviousStableIds = [] });
        fixture.Settings.DeckLinkPortOverrides.Add(new() { StableId = canonical.PreviousStableIds[0], IsOutputPort = true,
            StandbyPresetId = fixture.Settings.Presets[0].Id });
        await fixture.Store.SaveSettingsAsync(fixture.Settings);
        await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(fixture.Database,
            new(BenchOutputGuardTests.Bindings()), _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([])));
    }

    [Fact]
    public async Task ActualStoredReleasedRouteAndProtectedRoutePermitReleasedBench()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Store.UpsertPortAsync(BenchOutputGuardTests.Port(6));
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        // Use the application's serializer, including its numeric enums. A
        // handwritten string-state fixture does not represent the production DB.
        await fixture.Store.SaveRouteAsync(new("TEST/fixture/default/removed", "Synthetic removed", null, null,
            "1080i50", RouteState.Released, AssignmentMode.Manual, false, 0, 0, null, null, null, 0, 0, null, now, null, null));
        await fixture.Store.SaveRouteAsync(new("TEST/fixture/default/protected", "Synthetic protected", BenchOutputGuardTests.Port(6).StableId, null,
            "1080i50", RouteState.Running, AssignmentMode.Manual, false, 0, 0, null, null, null, 0, 0, null, now, null, null));
        await BenchReleasePreflight.EnsureReleasedAsync(fixture.Database, new(BenchOutputGuardTests.Bindings()),
            _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([]));
        Assert.Equal(2, (await fixture.Store.LoadRoutesAsync()).Count);
    }

    [Fact]
    public async Task ProvenRetiredNameAliasesDoNotDuplicatePhysicalConnectorOrChangeDatabase()
    {
        await using var fixture = await Fixture.Create();
        foreach (var n in Enumerable.Range(1, 6))
        {
            var canonical = BenchOutputGuardTests.Port(n) with
            { IsOutputPort = n == 6, PreviousStableIds = [DeckLinkIdentityResolver.LegacyStableId($"TEST-HANDLE-{n}")] };
            await fixture.Store.UpsertPortAsync(canonical);
            await fixture.Store.UpsertPortAsync(canonical with
            { StableId = canonical.PreviousStableIds[0], PreviousStableIds = [], IsOutputPort = false });
        }
        await fixture.Sql("PRAGMA wal_checkpoint(TRUNCATE)");
        var before = HashDatabase(fixture.Database);
        await BenchReleasePreflight.EnsureReleasedAsync(fixture.Database, new(BenchOutputGuardTests.Bindings()),
            _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([]));
        Assert.Equal(before, HashDatabase(fixture.Database));
    }

    [Theory]
    [InlineData("unlinked")]
    [InlineData("wrong-hash")]
    [InlineData("other-group")]
    [InlineData("other-subdevice")]
    [InlineData("other-persistent-id")]
    [InlineData("other-ffmpeg-target")]
    [InlineData("enabled-alias")]
    [InlineData("extra-alias")]
    public async Task UnprovenOrContradictoryDuplicateHandlesStillRefuseBench(string defect)
    {
        await using var fixture = await Fixture.Create();
        var original = BenchOutputGuardTests.Port(5) with
        { IsOutputPort = false, PreviousStableIds = [DeckLinkIdentityResolver.LegacyStableId("TEST-HANDLE-5")] };
        var retired = original with { StableId = original.PreviousStableIds[0], PreviousStableIds = [] };
        if (defect == "unlinked") original = original with { PreviousStableIds = [] };
        if (defect == "wrong-hash") { retired = retired with { StableId = "FFMPEG-NAME-NOT-THE-HASH" }; original = original with { PreviousStableIds = [retired.StableId] }; }
        if (defect == "other-group") retired = retired with { DeviceGroupId = "OTHER-CARD" };
        if (defect == "other-subdevice") retired = retired with { SubdeviceIndex = 5 };
        if (defect == "other-persistent-id") retired = retired with { PersistentId = "0x00000106" };
        if (defect == "other-ffmpeg-target") retired = retired with { FfmpegName = "TEST-HANDLE-6" };
        if (defect == "enabled-alias") retired = retired with { IsOutputPort = true };
        await fixture.Store.UpsertPortAsync(original);
        await fixture.Store.UpsertPortAsync(retired);
        if (defect == "extra-alias") await fixture.Store.UpsertPortAsync(retired with { StableId = "ANOTHER-ALIAS" });
        await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(fixture.Database,
            new(BenchOutputGuardTests.Bindings()), _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([])));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("7.5")]
    [InlineData("null")]
    public async Task InvalidNumericRouteStateCannotCertifyRelease(string state)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Sql("INSERT INTO routes(source_id,state,locked,json,updated_utc) VALUES('synthetic','Released',0,'"
            + "{\"sourceId\":\"TEST/fixture/default/source\",\"portId\":null,\"desiredPortId\":null,\"state\":" + state + "}', '2026-01-01')");
        await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(fixture.Database,
            new(BenchOutputGuardTests.Bindings()), _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([])));
    }

    [Theory]
    [InlineData("lowercase-id")]
    [InlineData("case-varied-property")]
    [InlineData("legacy-id")]
    [InlineData("missing-port")]
    [InlineData("missing-desired-port")]
    [InlineData("numeric-port")]
    [InlineData("invalid-state")]
    [InlineData("duplicate-property")]
    public async Task IncompleteOrNoncanonicalRouteEvidenceCannotCertifyRelease(string defect)
    {
        await using var fixture = await Fixture.Create();
        var row = new JsonObject { ["sourceId"] = "TEST/fixture/default/source", ["state"] = "WaitingForStream",
            ["portId"] = null, ["desiredPortId"] = null };
        if (defect == "lowercase-id") row["desiredPortId"] = BenchOutputGuardTests.Port(5).StableId.ToLowerInvariant();
        if (defect == "case-varied-property") { row.Remove("portId"); row["PortId"] = BenchOutputGuardTests.Port(5).StableId; }
        if (defect == "legacy-id")
        {
            await fixture.Store.UpsertPortAsync(BenchOutputGuardTests.Port(5) with { PreviousStableIds = ["LEGACY-FIFTH"] });
            row["desiredPortId"] = "LEGACY-FIFTH";
        }
        if (defect == "missing-port") row.Remove("portId");
        if (defect == "missing-desired-port") row.Remove("desiredPortId");
        if (defect == "numeric-port") row["portId"] = 123;
        if (defect == "invalid-state") row["state"] = "not-a-state";
        if (defect == "duplicate-property") row["PortId"] = BenchOutputGuardTests.Port(5).StableId;
        await fixture.Sql("INSERT INTO routes(source_id,state,locked,json,updated_utc) VALUES('synthetic','WaitingForStream',0,'" + row.ToJsonString() + "','2026-01-01')");
        var error = await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(
            fixture.Database, new(BenchOutputGuardTests.Bindings()), _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([])));
        Assert.Contains("Bench preflight refused", error.Message);
    }

    [Theory]
    [InlineData("ffmpeg.exe -i synthetic -f tee \"[f=decklink]TEST-HANDLE-5\"")]
    [InlineData("renamed.exe -i synthetic -f tee \"[f=decklink]Synthetic model (5)\"")]
    [InlineData("ffmpeg.exe -i synthetic -f fifo -fifo_format decklink TEST-HANDLE-5")]
    public async Task NestedDeckLinkOwnersCannotBeCertifiedAsReleased(string command)
    {
        await using var fixture = await Fixture.Create();
        var error = await Assert.ThrowsAsync<BenchSafetyException>(() => BenchReleasePreflight.EnsureReleasedAsync(
            fixture.Database, new(BenchOutputGuardTests.Bindings()),
            _ => Task.FromResult<IReadOnlyList<BenchProcessEvidence>>([new(123, "renamed.exe", command)])));
        Assert.Contains("Bench preflight refused", error.Message);
    }

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
