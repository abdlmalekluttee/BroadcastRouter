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
            Assert.Equal(SourceMediaExpectation.ContinuousVideo, declaration.Expectation);
            Assert.False(declaration.RequireAudio);
            Assert.False(migrated.Presets[0].IncludeAudio);
        });
    }

    [Fact]
    public async Task UpgradeDeclaresPersistedBehaviourOnceWithoutOverridingExplicitProfiles()
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
            Assert.Equal(4, migrated.SourceMediaProfiles.Count);
            Assert.Equal(SourceMediaExpectation.AudioOnly, SourceMediaPolicy.Get(migrated, "TEST/fixture/default/audio-led").Expectation);
            Assert.True(SourceMediaPolicy.Get(migrated, "TEST/fixture/default/audio-led").RequireAudio);
            Assert.Equal(SourceMediaExpectation.ContinuousVideo, SourceMediaPolicy.Get(migrated, "TEST/fixture/default/video").Expectation);
            Assert.Equal(SourceMediaExpectation.Undeclared, SourceMediaPolicy.Get(migrated, "TEST/fixture/default/unknown").Expectation);
            Assert.Equal(SourceMediaExpectation.SparseVideo, SourceMediaPolicy.Get(migrated, "TEST/fixture/default/explicit").Expectation);
            // After upgrade, a later discovery must not silently declare itself.
            await store.UpsertSourceAsync(new(new("TEST", "fixture", "default", "later"), "Later",
                new Uri("rtsp://example.invalid/later"), SourceState.Ready, 0, RecoveryReadinessTests.Av().Media));
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TABLE settings_writes(n INTEGER); CREATE TRIGGER count_settings AFTER UPDATE ON settings BEGIN INSERT INTO settings_writes VALUES(1); END;";
            await trigger.ExecuteNonQueryAsync();
            await RunStartupOnly(store, new(new ManualTimeProvider()));
            Assert.Equal(0, await Scalar(connection, "SELECT COUNT(*) FROM settings_writes"));
            Assert.Equal(SourceMediaExpectation.Undeclared, SourceMediaPolicy.Get(await store.LoadSettingsAsync(), "TEST/fixture/default/later").Expectation);
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

    private static RuntimeRoute Route(AssignmentMode assignment, RouteState state)
    {
        var when = DateTimeOffset.Parse("2024-12-31T23:59:00Z");
        return new("TEST/fixture/_default_/source-A", "Synthetic source", "synthetic-output", "Test output",
            "1080p25", state, assignment, false, 0, 1, 10, 25, 1, 0, 0,
            when, when, null, null, RetryAt: when.AddMinutes(2));
    }

    private static async Task RunStartupOnly(SqliteDataStore store, SupervisionClock clock)
    {
        // The real startup path loads/migrates/persists before its Host log. Stop at
        // that boundary, before any discovery, process, network or hardware loop.
        using var coordinator = new RouterCoordinator(store, null!, null!, new StartupBoundaryLogger(), clock);
        var method = typeof(RouterCoordinator).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.ThrowsAsync<StartupCompleteException>(() =>
            (Task)method.Invoke(coordinator, [CancellationToken.None])!);
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
        try
        {
            var file = Path.Combine(root, "test.db");
            var store = new SqliteDataStore(file);
            await store.InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
            await connection.OpenAsync();
            await run(store, connection);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
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
