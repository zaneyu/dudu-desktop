using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// The Preferences model no longer carries quiet hours, the local note daily
/// limit, reminder/evening flags, outfit/seasonal dates or the global
/// shortcut, but their columns stay in the schema (no migrations, no drops).
/// Migration 0001's NOT NULL no-default columns get fixed values on INSERT;
/// an UPDATE must leave every retired column exactly as an upgraded install
/// had it.
/// </summary>
public sealed class PreferencesLegacyColumnsTests
{
    private static readonly string[] RetiredColumns =
    [
        "quiet_hours_enabled",
        "quiet_hours_start",
        "quiet_hours_end",
        "local_note_daily_limit",
        "hydration_reminders_enabled",
        "break_reminders_enabled",
        "outfit_key",
        "automatic_seasonal_mode",
        "anniversary_month",
        "anniversary_day",
        "birthday_month",
        "birthday_day",
        "evening_check_in_enabled",
        "bedtime_ritual_enabled",
        "global_shortcut",
    ];

    [Fact]
    public async Task Fresh_database_saves_and_reloads_preferences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new PreferencesRepository(fixture.Database);
        var preferences = Preferences.Default with
        {
            Theme = AppTheme.Dark,
            ReducedMotion = true,
            LaunchAtSignIn = false,
            AlwaysOnTop = false,
            HidePetDuringFullscreen = false,
            AmbientMinimumInterval = TimeSpan.FromMinutes(42),
            SoundsEnabled = false,
            SoundVolume = 0.5,
            PauseMode = "OneHour",
            PauseExpiresUtc = new DateTimeOffset(2026, 9, 28, 10, 30, 0, TimeSpan.Zero),
        };

        Assert.Null(await repository.GetAsync(cancellationToken));
        await repository.SaveAsync(preferences, cancellationToken);

        Assert.Equal(preferences, await repository.GetAsync(cancellationToken));
    }

    [Fact]
    public async Task Fresh_database_insert_writes_fixed_values_for_the_not_null_legacy_columns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new PreferencesRepository(fixture.Database);

        await repository.SaveAsync(Preferences.Default, cancellationToken);

        var row = await ReadColumnsAsync(fixture.Database, cancellationToken, RetiredColumns);
        Assert.Equal(0L, row["quiet_hours_enabled"]);
        Assert.Equal("22:00", row["quiet_hours_start"]);
        Assert.Equal("07:00", row["quiet_hours_end"]);
        Assert.Equal(0L, row["local_note_daily_limit"]);

        // A second save is an UPDATE and still leaves them alone.
        await repository.SaveAsync(Preferences.Default with { ReducedMotion = true }, cancellationToken);
        Assert.Equal(row, await ReadColumnsAsync(fixture.Database, cancellationToken, RetiredColumns));
        Assert.True((await repository.GetAsync(cancellationToken))!.ReducedMotion);
    }

    [Fact]
    public async Task Upgraded_database_keeps_legacy_columns_untouched_on_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var schemaThree = await SchemaThreeFixture.CreateAsync(cancellationToken);
        await SeedSchemaThreePreferencesAsync(schemaThree.Options, cancellationToken);

        await using var database = await Database.OpenAsync(schemaThree.Options, cancellationToken);
        await ExecuteAsync(database, """
            UPDATE preferences SET
                outfit_key = 'winter',
                automatic_seasonal_mode = 0,
                anniversary_month = 2, anniversary_day = 14,
                birthday_month = 11, birthday_day = 3,
                evening_check_in_enabled = 1,
                bedtime_ritual_enabled = 1,
                global_shortcut = 'Ctrl+Alt+D'
            WHERE id = 1;
            """, cancellationToken);
        var legacyBefore = await ReadColumnsAsync(database, cancellationToken, RetiredColumns);
        var repository = new PreferencesRepository(database);

        var loaded = await repository.GetAsync(cancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.True(loaded.ReducedMotion);
        Assert.Equal(TimeSpan.FromMinutes(20), loaded.AmbientMinimumInterval);

        var updated = loaded with
        {
            Theme = AppTheme.Dark,
            ReducedMotion = false,
            SoundVolume = 0.8,
            PauseMode = "Indefinite",
        };
        await repository.SaveAsync(updated, cancellationToken);

        Assert.Equal(updated, await repository.GetAsync(cancellationToken));
        var legacyAfter = await ReadColumnsAsync(database, cancellationToken, RetiredColumns);
        Assert.Equal(legacyBefore, legacyAfter);
        Assert.Equal(1L, legacyAfter["quiet_hours_enabled"]);
        Assert.Equal("21:30", legacyAfter["quiet_hours_start"]);
        Assert.Equal("06:45", legacyAfter["quiet_hours_end"]);
        Assert.Equal(5L, legacyAfter["local_note_daily_limit"]);
        Assert.Equal(0L, legacyAfter["hydration_reminders_enabled"]);
        Assert.Equal("winter", legacyAfter["outfit_key"]);
        Assert.Equal("Ctrl+Alt+D", legacyAfter["global_shortcut"]);
    }

    /// <summary>A schema-3 install's own preferences row (migrations 0001 +
    /// 0002 columns only), written the way that build wrote it.</summary>
    private static async Task SeedSchemaThreePreferencesAsync(DatabaseOptions options, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(Database.ConnectionString(options));
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO preferences
                    (id, theme, quiet_hours_enabled, quiet_hours_start, quiet_hours_end, reduced_motion,
                     local_note_daily_limit, launch_at_sign_in, always_on_top, hide_pet_during_fullscreen,
                     ambient_minimum_interval_ticks, hydration_reminders_enabled, break_reminders_enabled)
                VALUES (1, 1, 1, '21:30', '06:45', 1, 5, 1, 1, 1, $interval, 0, 1);
                """;
            command.Parameters.AddWithValue("$interval", TimeSpan.FromMinutes(20).Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);
    }

    private static async Task ExecuteAsync(Database database, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await database.CreateConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, object?>> ReadColumnsAsync(
        Database database,
        CancellationToken cancellationToken,
        IReadOnlyList<string> columns)
    {
        await using var connection = await database.CreateConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns)} FROM preferences WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return row;
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string root, Database database)
        {
            Root = root;
            Database = database;
        }

        private string Root { get; }
        public Database Database { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            return new DatabaseFixture(root, database);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
