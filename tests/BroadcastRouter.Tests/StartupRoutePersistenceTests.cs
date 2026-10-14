using System.Reflection;
using BroadcastRouter.Application;
using BroadcastRouter.Domain;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BroadcastRouter.Tests;

public sealed class StartupRoutePersistenceTests
{
    [Fact]
    public async Task AutomaticDetectionUpgradeDoesNotWriteStaticDeclarations()
    {
        await WithStore(async (store, connection) =>
        {
            await store.SaveSettingsAsync(new());
            await store.UpsertSourceAsync(new(new("TEST", "fixture", "default", "synthetic"), "Synthetic",
                new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0, RecoveryReadinessTests.Av().Media));
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TABLE settings_writes(n INTEGER); CREATE TRIGGER count_settings AFTER UPDATE ON settings BEGIN INSERT INTO settings_writes VALUES(1); END;";
            await trigger.ExecuteNonQueryAsync();
            await RunStartupOnly(store, new(new ManualTimeProvider()));
            Assert.Equal(0, await Scalar(connection, "SELECT COUNT(*) FROM settings_writes"));
            Assert.Empty((await store.LoadSettingsAsync()).SourceMediaProfiles);
        });
    }

    [Fact]
    public async Task UpgradePreservesExistingMutedVideoAssignment()
    {
        await WithStore(async (store, connection) =>
        {
            var settings = new OperatorSettings();
            settings.Presets[0].IncludeAudio = false;
            await store.SaveSettingsAsync(settings);
            var route = Route(AssignmentMode.Manual, RouteState.WaitingForStream) with { PresetId = settings.Presets[0].Id };
            await store.SaveRouteAsync(route, route.State);
            await store.UpsertSourceAsync(new(new("TEST", "fixture", "_default_", "source-A"), "Synthetic source",
                new Uri("rtsp://example.invalid/test"), SourceState.Ready, 0, RecoveryReadinessTests.Av().Media));
            await RunStartupOnly(store, new(new ManualTimeProvider()));
            var migrated = await store.LoadSettingsAsync();
            var declaration = SourceMediaPolicy.Get(migrated, route.SourceId);
            Assert.Equal(SourceMediaOverride.Auto, declaration.Override);
            Assert.False(declaration.RequireAudio);
            Assert.False(migrated.Presets[0].IncludeAudio);
        });
    }

    [Fact]
    public async Task UpgradeIgnoresLegacyDeclarationsAndKeepsAutoWithoutSettingsWrites()
    {
        await WithStore(async (store, connection) =>
        {
            var settings = new OperatorSettings();
            settings.SourceMediaProfiles.Add(new() { SourceId = "TEST/fixture/default/explicit", Expectation = SourceMediaExpectation.SparseVideo });
            await store.SaveSettingsAsync(settings);
            foreach (var name in new[] { "audio-led", "video", "unknown", "explicit" })
            {
                var media = name == "unknown" ? null : RecoveryReadinessTests.Av().Media! with { HasUsableVideo = name != "audio-led" };
                await store.UpsertSourceAsync(new(new("TEST", "fixture", "default", name), name,
                    new Uri("rtsp://example.invalid/test"), SourceState.PublisherDisconnected, 0, media));
            }
            await RunStartupOnly(store, new(new ManualTimeProvider()));
            var migrated = await store.LoadSettingsAsync();
            Assert.Single(migrated.SourceMediaProfiles);
            foreach (var name in new[] { "audio-led", "video", "unknown", "explicit" })
                Assert.Equal(SourceMediaOverride.Auto, SourceMediaPolicy.Get(migrated, "TEST/fixture/default/" + name).Override);
            // Later discoveries are also automatic and cause no declaration writes.
            await store.UpsertSourceAsync(new(new("TEST", "fixture", "default", "later"), "Later",
                new Uri("rtsp://example.invalid/later"), SourceState.Ready, 0, RecoveryReadinessTests.Av().Media));
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TABLE settings_writes(n INTEGER); CREATE TRIGGER count_settings AFTER UPDATE ON settings BEGIN INSERT INTO settings_writes VALUES(1); END;";
            await trigger.ExecuteNonQueryAsync();
            await RunStartupOnly(store, new(new ManualTimeProvider()));
            Assert.Equal(0, await Scalar(connection, "SELECT COUNT(*) FROM settings_writes"));
            Assert.Equal(SourceMediaOverride.Auto, SourceMediaPolicy.Get(await store.LoadSettingsAsync(), "TEST/fixture/default/later").Override);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100000000)]
    public async Task RebasedUnchangedRouteIsNotWrittenAtStartup(long offsetTicks)
    {
        await WithStore(async (store, connection) =>
        {
            var original = Route(AssignmentMode.Automatic, RouteState.Reconnecting);
            await store.SaveRouteAsync(original, original.State);
            await CountFutureWrites(connection);
            var provider = new ManualTimeProvider();
            var clock = new SupervisionClock(provider);
            provider.StepUtc(TimeSpan.FromTicks(offsetTicks));
            await RunStartupOnly(store, clock);
            await RunStartupOnly(store, clock);
            Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM startup_writes"));
            Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM route_history"));
            Assert.Equal(original, Assert.Single(await store.LoadRoutesAsync()));
        });
    }

    [Fact]
    public async Task LegacyAssignmentAndRealTransientResetAreStillPersisted()
    {
        await WithStore(async (store, connection) =>
        {
            var original = Route(AssignmentMode.Manual, RouteState.Running);
            await store.SaveRouteAsync(original, original.State);
            await CountFutureWrites(connection);
            var provider = new ManualTimeProvider();
            var clock = new SupervisionClock(provider);
            provider.StepUtc(TimeSpan.FromTicks(1));
            await RunStartupOnly(store, clock);
            var saved = Assert.Single(await store.LoadRoutesAsync());
            Assert.Equal(original.PortId, saved.DesiredPortId);
            Assert.Equal(original.PortName, saved.DesiredPortName);
            Assert.True(saved.ReserveWhileOffline);
            Assert.False(saved.AllowTemporaryUse);
            Assert.Equal(RouteState.WaitingForStream, saved.State);
            Assert.Null(saved.PortId);
            Assert.Null(saved.StartedAt);
            Assert.Null(saved.RetryAt);
            Assert.Null(saved.Frame);
            Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM startup_writes"));
            Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM route_history"));
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task ClockStepKeepsActualAuditUtcAndPersistedRetryRemainingTime(int step)
    {
        await WithStore(async (store, connection) =>
        {
            var provider = new ManualTimeProvider();
            var clock = new SupervisionClock(provider);
            var original = Route(AssignmentMode.Automatic, RouteState.Reconnecting) with
            {
                StartedAt = clock.Now,
                UpdatedAt = clock.Now,
                RetryAt = clock.Now.AddSeconds(30),
                HoldOutputWhilePublisherLive = true
            };
            await store.SaveRouteAsync(original, original.State);
            using var coordinator = new RouterCoordinator(store, null!, null!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RouterCoordinator>.Instance, clock);
            var routes = (Dictionary<string, RuntimeRoute>)typeof(RouterCoordinator)
                .GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
            routes.Add(original.SourceId, original);
            provider.Advance(TimeSpan.FromSeconds(5));
            provider.StepUtc(TimeSpan.FromSeconds(step));

            // Real command, atomic audit/route transaction and public snapshot;
            // no network, child or hardware loop is started by this command.
            await coordinator.CommandAsync("disable-live-hold", original.SourceId, actor: "synthetic-admin");
            var audit = Assert.Single(await store.ReadConfigurationAuditAsync());
            Assert.Equal("PublisherLiveRecoveryHold", audit.EventType);
            Assert.Equal(provider.GetUtcNow(), audit.Timestamp);
            Assert.Equal(provider.GetUtcNow(), coordinator.Snapshot.UpdatedAt);
            var saved = Assert.Single(await store.LoadRoutesAsync());
            Assert.False(saved.HoldOutputWhilePublisherLive);
            Assert.Equal(provider.GetUtcNow(), saved.UpdatedAt);
            Assert.Equal(provider.GetUtcNow().AddSeconds(25), saved.RetryAt);
            Assert.Equal(TimeSpan.FromSeconds(25), routes[original.SourceId].RetryAt - clock.Now);

            await CountFutureWrites(connection);
            var nextClock = new SupervisionClock(provider);
            provider.Advance(TimeSpan.FromSeconds(3));
            // An additional adjustment between clock construction and startup
            // makes an omitted RouteFromUtc consumer conversion observable.
            provider.StepUtc(TimeSpan.FromSeconds(step));
            var loaded = Assert.Single(await RunStartupOnly(store, nextClock));
            Assert.Equal(TimeSpan.FromSeconds(22 - step), loaded.RetryAt - nextClock.Now);
            Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM startup_writes"));
            Assert.Equal(0L, await Scalar(connection, "SELECT COUNT(*) FROM route_history"));
            Assert.Equal(saved, Assert.Single(await store.LoadRoutesAsync()));
        });
    }

    private static RuntimeRoute Route(AssignmentMode assignment, RouteState state)
    {
        var when = DateTimeOffset.Parse("2024-12-31T23:59:00Z");
        return new("TEST/fixture/_default_/source-A", "Synthetic source", "synthetic-output", "Test output",
            "1080p25", state, assignment, false, 0, 1, 10, 25, 1, 0, 0,
            when, when, null, null, RetryAt: when.AddMinutes(2));
    }

    private static async Task<RuntimeRoute[]> RunStartupOnly(SqliteDataStore store, SupervisionClock clock)
    {
        // The real startup path loads/migrates/persists before its Host log. Stop at
        // that boundary, before any discovery, process, network or hardware loop.
        using var coordinator = new RouterCoordinator(store, null!, null!, new StartupBoundaryLogger(), clock);
        var method = typeof(RouterCoordinator).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.ThrowsAsync<StartupCompleteException>(() =>
            (Task)method.Invoke(coordinator, [CancellationToken.None])!);
        return ((Dictionary<string, RuntimeRoute>)typeof(RouterCoordinator)
            .GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!).Values.ToArray();
    }

    private sealed class StartupCompleteException : Exception;
    private sealed class StartupBoundaryLogger : ILogger<RouterCoordinator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields
                && fields.Any(field => field.Key == "Category" && Equals(field.Value, "Host")))
                throw new StartupCompleteException();
        }
    }

    private static async Task WithStore(Func<SqliteDataStore, SqliteConnection, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "router-startup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteDataStore? store = null;
        try
        {
            var file = Path.Combine(root, "test.db");
            store = new SqliteDataStore(file, readDiagnosticVolumeSpace: TelemetryTestVolume.Read);
            await store.InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
            await connection.OpenAsync();
            await run(store, connection);
        }
        finally
        {
            // This fixture owns the store, unlike the coordinator which borrows
            // it from the host. Drain its diagnostic writer before deleting it.
            if (store is not null) await store.StopDiagnosticsAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    private static async Task CountFutureWrites(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE startup_writes (source_id TEXT);
            CREATE TRIGGER count_startup_writes AFTER UPDATE ON routes
            BEGIN INSERT INTO startup_writes VALUES (NEW.source_id); END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
