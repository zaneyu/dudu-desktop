using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// The Appearance page's global shortcut used to be applied at runtime only, so every restart
/// silently went back to Ctrl+Alt+D. It now lives in preferences.global_shortcut (migration 12).
/// </summary>
public sealed class GlobalShortcutPreferenceTests
{
    [Fact]
    public async Task Preferences_round_trip_a_custom_global_shortcut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ShortcutDatabaseFixture.CreateAsync(cancellationToken);
        var repository = new PreferencesRepository(fixture.Database);
        var preferences = Preferences.Default with { GlobalShortcut = "Ctrl+Shift+F9", SoundVolume = 0.5 };

        await repository.SaveAsync(preferences, cancellationToken);

        Assert.Equal(preferences, await repository.GetAsync(cancellationToken));
        // And through a freshly opened database, i.e. what the next app start reads.
        await using var reopened = await Database.OpenAsync(fixture.Options, cancellationToken);
        Assert.Equal(
            "Ctrl+Shift+F9",
            (await new PreferencesRepository(reopened).GetAsync(cancellationToken))!.GlobalShortcut);
    }

    [Fact]
    public async Task Clearing_the_global_shortcut_persists_the_default_marker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ShortcutDatabaseFixture.CreateAsync(cancellationToken);
        var repository = new PreferencesRepository(fixture.Database);
        await repository.SaveAsync(Preferences.Default with { GlobalShortcut = "Ctrl+Alt+K" }, cancellationToken);

        await repository.SaveAsync(Preferences.Default with { GlobalShortcut = "   " }, cancellationToken);

        Assert.Null((await repository.GetAsync(cancellationToken))!.GlobalShortcut);
    }

    [Fact]
    public async Task Version_eleven_database_migrates_to_an_unset_global_shortcut_keeping_other_preferences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ShortcutDatabaseFixture.CreateAsync(cancellationToken);
        var repository = new PreferencesRepository(fixture.Database);
        var existing = Preferences.Default with { Theme = AppTheme.Dark, SoundVolume = 0.6 };
        await repository.SaveAsync(existing, cancellationToken);
        await using (var connection = await fixture.Database.CreateConnectionAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE preferences DROP COLUMN global_shortcut; UPDATE schema_version SET version = 11 WHERE id = 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        fixture.Database.InvalidateInitialization();
        var migrated = await repository.GetAsync(cancellationToken);

        Assert.Equal(existing, migrated);
        Assert.Null(migrated!.GlobalShortcut);
        await using var versionConnection = await fixture.Database.CreateConnectionAsync(cancellationToken);
        await using var version = versionConnection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_version WHERE id = 1;";
        Assert.Equal(12L, Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken)));
    }

    [Fact]
    public void Global_shortcut_migration_is_embedded_as_version_twelve()
    {
        var runner = new MigrationRunner(new DatabaseOptions(
            Path.Combine(Path.GetTempPath(), "unused.db"),
            Path.Combine(Path.GetTempPath(), "unused-backups")));

        var migration = Assert.Single(runner.Migrations, item => item.Version == 12);
        Assert.Contains("global_shortcut", migration.Sql, StringComparison.Ordinal);
    }

    private sealed class ShortcutDatabaseFixture : IAsyncDisposable
    {
        private readonly string _root;

        private ShortcutDatabaseFixture(string root, DatabaseOptions options, Database database)
        {
            _root = root;
            Options = options;
            Database = database;
        }

        public DatabaseOptions Options { get; }
        public Database Database { get; }

        public static async Task<ShortcutDatabaseFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options, cancellationToken);
            return new ShortcutDatabaseFixture(root, options, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
