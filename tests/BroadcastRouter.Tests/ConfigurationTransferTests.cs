using System.Reflection;
using System.Text.Json;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class ConfigurationTransferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveStandbyUrlCannotBypassCredentialExclusion(bool queryToken)
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        var settings = new OperatorSettings();
        settings.Presets[0].StandbyMode = FallbackMode.Black;
        settings.Presets[0].StandbyValue = queryToken
            ? "rtsp://localhost/live/fixture?token=private-token"
            : "rtsp://user:private-password@localhost/live/fixture";
        await fixture.Store.SaveSettingsAsync(settings);
        if (queryToken)
            await Assert.ThrowsAsync<InvalidDataException>(() => Export(fixture.Store.DatabasePath));
        else
            Assert.DoesNotContain("private-password", await Export(fixture.Store.DatabasePath));
    }

    [Theory]
    [InlineData("presets", "includeAudio")]
    [InlineData("wowzaServers", "validateTlsCertificate")]
    public async Task MissingNestedFieldCannotSilentlyEnableAudioOrDisableTls(string list, string field)
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        var settings = new OperatorSettings { WowzaServers = [new()] };
        settings.Presets[0].IncludeAudio = false;
        await fixture.Store.SaveSettingsAsync(settings);
        var document = System.Text.Json.Nodes.JsonNode.Parse(await Export(fixture.Store.DatabasePath))!;
        Assert.True(document[list]![0]!.AsObject().Remove(field));
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(document.ToJsonString(), Path.Combine(fixture.Root, "fresh.db")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
    }

    [Fact]
    public async Task NullRequiredNestedValueIsInvalidData()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await Export(fixture.Store.DatabasePath))!;
        document["presets"]![0]!["name"] = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(document.ToJsonString(), Path.Combine(fixture.Root, "fresh.db")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
    }

    [Fact]
    public async Task ImportCannotHideQueryCredentialInInactiveStandbyValue()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await Export(fixture.Store.DatabasePath))!;
        document["presets"]![0]!["standbyValue"] = "rtsp://localhost/live/fixture?token=private-token";
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(document.ToJsonString(), Path.Combine(fixture.Root, "fresh.db")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
    }

    [Fact]
    public async Task DistinctCaseSensitiveSourceIdentitiesRoundTrip()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings
        { DeckLinkPortOverrides = [new() { StableId = "OUT", IsOutputPort = true, StandbyPresetId = "1080p25" }] });
        foreach (var name in new[] { "Feed", "feed" })
            await fixture.Store.SaveRouteAsync(new RuntimeRoute("TEST/live/default/" + name, name, "OUT", "Output", "1080p25",
                RouteState.WaitingForStream, AssignmentMode.Manual, false, 1, 0, null, null, null, 0, 0, null,
                fixture.Clock.UtcNow, null, null, DesiredPortId: "OUT"));
        var destination = Path.Combine(fixture.Root, "fresh.db");
        await Import(await Export(fixture.Store.DatabasePath), destination);
        var fresh = new SqliteDataStore(destination, readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        try
        {
            await fresh.InitializeAsync();
            Assert.Equal(new[] { "TEST/live/default/Feed", "TEST/live/default/feed" },
                (await fresh.LoadRoutesAsync()).Select(r => r.SourceId).OrderBy(id => id, StringComparer.Ordinal));
        }
        finally { await fresh.StopDiagnosticsAsync(); }
    }

    private static MethodInfo Transfer(string method)
    {
        var type = typeof(SqliteDataStore).Assembly.GetType("BroadcastRouter.Infrastructure.ConfigurationTransfer");
        Assert.NotNull(type);
        var found = type.GetMethod(method);
        Assert.NotNull(found);
        return found;
    }

    private static Task<string> Export(string path) => (Task<string>)Transfer("ExportAsync").Invoke(null, [path, CancellationToken.None])!;
    private static Task Import(string json, string path) => (Task)Transfer("ImportAsync").Invoke(null, [json, path, CancellationToken.None])!;

    [Theory]
    [InlineData("null")]
    [InlineData("{\"formatVersion\":1}")]
    [InlineData("{\"formatVersion\":99}")]
    public async Task IncompleteOrUnsupportedDocumentsFailAsInvalidData(string json)
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        var target = Path.Combine(fixture.Root, "fresh.db");
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(json, target));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
    }

    [Fact]
    public async Task OrphanedDestinationSidecarsMustNotBeAdoptedOrDeleted()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        var json = await Export(fixture.Store.DatabasePath);
        var target = Path.Combine(fixture.Root, "fresh.db");
        await File.WriteAllTextAsync(target + "-wal", "unrelated file");
        await Assert.ThrowsAsync<IOException>(() => Import(json, target));
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(target + "-wal"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ImportRejectsUnknownSecretFieldsAndLeavesNoDatabase()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        var document = System.Text.Json.Nodes.JsonNode.Parse(await Export(fixture.Store.DatabasePath))!;
        document["protectedPassword"] = "forbidden";
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(document.ToJsonString(), Path.Combine(fixture.Root, "fresh.db")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
    }

    [Fact]
    public async Task ConfigurationRoundTripExcludesSecretsHistoryAndRuntimeState()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        var settings = new OperatorSettings
        {
            WowzaServers = [new() { ServerId = "SYNTHETIC", FriendlyName = "Synthetic server", Username = "operator",
                ProtectedPassword = "DO-NOT-TRANSFER-CIPHERTEXT", ManagementUrl = "http://localhost:8087/" }],
            DeckLinkPortOverrides = [new() { StableId = "OUT-A", IsOutputPort = true, StandbyPresetId = "1080i50", StandbyLabel = "Synthetic label" }],
            Rules = [new() { Id = "rule-a", FixedPortId = "OUT-A", PresetId = "1080i50" }],
            DeckLinkCardOverrides = [new() { DeviceGroupId = "CARD-A", FriendlyName = "Synthetic card" }],
            ManualSources = [new() { StableId = "MANUAL-A", FriendlyName = "Manual fixture", FixedPortId = "OUT-A" }],
            SourceMediaProfiles = [new() { SourceId = "SYNTHETIC/live/default/test", Override = SourceMediaOverride.ForceAudioOnly }]
        };
        await fixture.Store.SaveSettingsAsync(settings);
        await fixture.Scalar("UPDATE settings SET json=replace(json,'http://localhost:8087/','http://user:secret@localhost:8087/'); SELECT 1;");
        var route = new RuntimeRoute("SYNTHETIC/live/default/test", "Synthetic", "OUT-A", "Output A", "1080i50",
            RouteState.Running, AssignmentMode.Manual, true, 50, 9, 1000, 25, 1, 99, 99,
            fixture.Clock.UtcNow, fixture.Clock.UtcNow, "old failure", "do-not-transfer-history",
            DesiredPortId: "OUT-A", DesiredPortName: "Output A");
        await fixture.Store.SaveRouteAsync(route);
        await fixture.Store.SaveRouteAsync(route with { SourceId = "automatic", AssignmentMode = AssignmentMode.Automatic, DesiredPortId = null });
        await fixture.Store.WriteLogAsync("Information", "Private", "do-not-transfer-logs");
        var json = await Export(fixture.Store.DatabasePath);
        Assert.DoesNotContain("DO-NOT-TRANSFER", json);
        Assert.DoesNotContain("secret@", json);
        Assert.DoesNotContain("do-not-transfer", json);
        Assert.DoesNotContain("protectedPassword", json, StringComparison.OrdinalIgnoreCase);
        var target = Path.Combine(fixture.Root, "fresh.db");
        await Import(json, target);
        var imported = new SqliteDataStore(target, readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
        try
        {
            await imported.InitializeAsync();
            var actual = await imported.LoadSettingsAsync();
            Assert.Equal("", Assert.Single(actual.WowzaServers).ProtectedPassword);
            Assert.False(actual.WowzaServers[0].Enabled);
            Assert.Equal("http://localhost:8087/", actual.WowzaServers[0].ManagementUrl);
            Assert.False(actual.Routing.AutomaticRoutingEnabled);
            Assert.True(actual.Security.RequireAuthentication);
            Assert.Equal("", actual.MediaTools.FfmpegPath);
            Assert.Equal("Synthetic label", Assert.Single(actual.DeckLinkPortOverrides).StandbyLabel);
            Assert.Equal("Synthetic card", Assert.Single(actual.DeckLinkCardOverrides).FriendlyName);
            Assert.False(Assert.Single(actual.ManualSources).Enabled);
            Assert.Equal(SourceMediaOverride.ForceAudioOnly, Assert.Single(actual.SourceMediaProfiles).Override);
            Assert.Equal("rule-a", Assert.Single(actual.Rules).Id);
            Assert.Equal(settings.Presets.Count, actual.Presets.Count);
            var intent = Assert.Single(await imported.LoadRoutesAsync());
            Assert.Equal("OUT-A", intent.DesiredPortId);
            Assert.Equal(RouteState.WaitingForStream, intent.State);
            Assert.Null(intent.PortId);
            Assert.Null(intent.Frame);
            Assert.Equal(0, intent.RestartCount);
            Assert.Empty(await imported.ReadLogsAsync());
            await using var db = new SqliteConnection($"Data Source={target};Mode=ReadOnly;Pooling=False");
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM route_history";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally { await imported.StopDiagnosticsAsync(); }
        await Assert.ThrowsAsync<IOException>(() => Import(json, target));
    }

    [Fact]
    public async Task FailedValidationAfterStagingLeavesDestinationAbsentAndSourceUnchanged()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        var original = await Export(fixture.Store.DatabasePath);
        var document = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        document["presets"]![0]!["width"] = -1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(document.ToJsonString(), Path.Combine(fixture.Root, "fresh.db")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "fresh.db*"));
        Assert.Equal(original, await Export(fixture.Store.DatabasePath));
    }

    [Fact]
    public async Task ExportDoesNotInitializeOrModifyTheSourceDatabase()
    {
        await using var fixture = await DiagnosticRetentionTests.Fixture.Create();
        await fixture.Store.SaveSettingsAsync(new OperatorSettings());
        await fixture.Store.StopDiagnosticsAsync();
        await fixture.Scalar("PRAGMA wal_checkpoint(TRUNCATE); SELECT 1;");
        using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Store.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString()))
            SqliteConnection.ClearPool(pooled);
        var before = await Hash(fixture.Store.DatabasePath);
        _ = await Export(fixture.Store.DatabasePath);
        Assert.Equal(before, await Hash(fixture.Store.DatabasePath));
    }

    private static async Task<byte[]> Hash(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await System.Security.Cryptography.SHA256.HashDataAsync(file);
    }
}
