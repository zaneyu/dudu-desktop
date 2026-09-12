using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

public sealed class DatabaseTests
{
    [Fact]
    public async Task Migration_creates_all_version_one_tables()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var names = await fixture.ReadTableNamesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("schema_version", names);
        Assert.Contains("reminders", names);
        Assert.Contains("focus_sessions", names);
        Assert.Contains("remote_envelopes", names);
        Assert.Contains("processed_remote_messages", names);
        Assert.Contains("mood_check_ins", names);
    }

    [Fact]
    public async Task Failed_migration_rolls_back_and_preserves_original_database()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var connection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken);
        var runner = new MigrationRunner(fixture.Options);
        runner.AddMigration(2, "CREATE TABLE broken(;" );

        await Assert.ThrowsAsync<SqliteException>(() => runner.RunAsync(connection, TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task Backup_rotation_keeps_five_newest_valid_backups()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        for (var i = 0; i < 7; i++)
        {
            await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.Equal(5, Directory.GetFiles(fixture.Options.BackupDirectory, "*.db").Length);
    }

    [Fact]
    public async Task First_database_contains_the_twelve_private_default_notes_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await fixture.Database.InitializeAsync(TestContext.Current.CancellationToken);

        var notes = await new LocalNoteRepository(fixture.Database).ListEnabledAsync(TestContext.Current.CancellationToken);
        Assert.Equal(12, notes.Count);
        Assert.Equal(12, notes.Select(note => note.Text).Distinct().Count());
    }

    [Fact]
    public async Task Restore_rejects_backup_that_fails_integrity_check()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var corrupt = Path.Combine(fixture.Options.BackupDirectory, "corrupt.db");
        await File.WriteAllTextAsync(corrupt, "not sqlite", TestContext.Current.CancellationToken);

        var result = await fixture.Backups.TryRestoreAsync(corrupt, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.IntegrityCheckFailed, result.Failure);
        Assert.True(File.Exists(corrupt));
    }

    [Fact]
    public async Task Local_note_cap_is_atomic_between_two_connections()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repositoryA = new LocalNoteRepository(fixture.Database);
        var repositoryB = new LocalNoteRepository(fixture.Database);
        var date = new DateOnly(2026, 9, 11);
        var now = DateTimeOffset.Parse("2026-09-11T10:00:00Z");

        var results = await Task.WhenAll(
            repositoryA.TryRecordShownAsync("default-note-01", now, date, 1, true, TestContext.Current.CancellationToken),
            repositoryB.TryRecordShownAsync("default-note-02", now.AddSeconds(1), date, 1, true, TestContext.Current.CancellationToken));

        Assert.Single(results, result => result);
        Assert.Equal(1, await repositoryA.CountUnsolicitedShownAsync(date, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Repositories_round_trip_utc_and_optional_values()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var taskRepository = new TaskRepository(fixture.Database);
        var task = new TaskItem(
            Guid.NewGuid(), "task", null,
            DateTimeOffset.Parse("2026-09-12T09:30:00-07:00"), false,
            DateTimeOffset.Parse("2026-09-11T10:00:00-07:00"),
            DateTimeOffset.Parse("2026-09-11T10:01:00-07:00"), null);
        await taskRepository.SaveAsync(task, cancellationToken);
        Assert.Equal(task, await taskRepository.GetAsync(task.Id, cancellationToken));

        var focusRepository = new FocusSessionRepository(fixture.Database);
        var focus = new FocusSession(Guid.NewGuid(), task.Id,
            DateTimeOffset.Parse("2026-09-11T17:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T17:25:00Z"), TimeSpan.FromMinutes(3),
            FocusStatus.Paused, DateTimeOffset.Parse("2026-09-11T17:04:00Z"));
        await focusRepository.SaveAsync(focus, cancellationToken);
        Assert.Equal(focus, await focusRepository.GetAsync(focus.Id, cancellationToken));

        var reminderRepository = new ReminderRepository(fixture.Database);
        var reminder = new Reminder("reminder", "title", null, true,
            new RecurrenceRule.SelectedWeekdays([DayOfWeek.Monday, DayOfWeek.Friday], new TimeOnly(8, 5)),
            "UTC", QuietHoursBehavior.WaitUntilQuietHoursEnd, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-11T08:05:00Z"), null,
            new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)));
        await reminderRepository.SaveAsync(reminder, cancellationToken);
        var loadedReminder = (await reminderRepository.LoadDueAsync(
            DateTimeOffset.Parse("2026-09-11T09:00:00Z"), cancellationToken)).Single();
        Assert.Equal(reminder.Id, loadedReminder.Id);
        Assert.Equal(reminder.NextDueUtc, loadedReminder.NextDueUtc);
        var loadedWeekdays = Assert.IsType<RecurrenceRule.SelectedWeekdays>(loadedReminder.Rule);
        Assert.Equal(((RecurrenceRule.SelectedWeekdays)reminder.Rule).LocalTime, loadedWeekdays.LocalTime);
        Assert.Equal(((RecurrenceRule.SelectedWeekdays)reminder.Rule).Days, loadedWeekdays.Days);
        Assert.Equal(reminder.QuietHours, loadedReminder.QuietHours);

        var countdownRepository = new CountdownRepository(fixture.Database);
        var countdown = new Countdown("countdown", "event",
            DateTimeOffset.Parse("2026-10-01T10:00:00-07:00"), null, false, TimeZoneInfo.Utc);
        await countdownRepository.SaveAsync(countdown, cancellationToken);
        var loadedCountdown = await countdownRepository.GetAsync(countdown.Id, cancellationToken);
        Assert.Equal(countdown, loadedCountdown);

        var preferences = new Preferences(AppTheme.Dark,
            new QuietHours(true, new TimeOnly(23, 0), new TimeOnly(6, 0)), true, 3, true, false, true,
            TimeSpan.FromMinutes(15));
        var preferencesRepository = new PreferencesRepository(fixture.Database);
        await preferencesRepository.SaveAsync(preferences, cancellationToken);
        Assert.Equal(preferences, await preferencesRepository.GetAsync(cancellationToken));

        var profile = new Profile("Mia", true);
        var profileRepository = new ProfileRepository(fixture.Database);
        await profileRepository.SaveAsync(profile, cancellationToken);
        Assert.Equal(profile, await profileRepository.GetAsync(cancellationToken));

        var placement = new PetPlacement("DISPLAY-1", .25, .75, 1.2);
        var placementRepository = new PetPlacementRepository(fixture.Database);
        await placementRepository.SaveAsync(placement, cancellationToken);
        Assert.Equal(placement, await placementRepository.GetAsync(placement.MonitorDeviceName, cancellationToken));

        var checkIn = new MoodCheckIn(Guid.NewGuid(), MoodChoice.Tired, null,
            DateTimeOffset.Parse("2026-09-11T17:00:00Z"));
        var checkInRepository = new CheckInRepository(fixture.Database);
        await checkInRepository.SaveAsync(checkIn, cancellationToken);
        Assert.Equal(checkIn, Assert.Single(await checkInRepository.ListSinceAsync(
            DateTimeOffset.Parse("2026-09-11T16:00:00Z"), cancellationToken)));

        var envelope = new RemoteEnvelope("message-1", [1, 2, 3], null, [4], [5], null,
            DateTimeOffset.Parse("2026-09-11T17:00:00Z"));
        var envelopeRepository = new RemoteEnvelopeRepository(fixture.Database);
        Assert.True(await envelopeRepository.TryInsertAsync(envelope, cancellationToken));
        var loadedEnvelope = await envelopeRepository.GetAsync(envelope.MessageId, cancellationToken);
        Assert.Equal(envelope.MessageId, loadedEnvelope?.MessageId);
        Assert.Equal(envelope.Ciphertext, loadedEnvelope?.Ciphertext);
    }

    [Fact]
    public async Task Unit_of_work_rolls_back_and_commits_multiple_repository_writes()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var unitOfWork = new AppUnitOfWork(fixture.Database);
        var preferences = new Preferences(AppTheme.Dark, new QuietHours(true, new TimeOnly(22), new TimeOnly(7)), false, 3, false, false, true, TimeSpan.FromMinutes(15));

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync(async (context, cancellationToken) =>
        {
            await context.Profiles.SaveAsync(new Profile("Rollback", true), cancellationToken);
            await context.Preferences.SaveAsync(preferences, cancellationToken);
            throw new InvalidOperationException("rollback");
        }, TestContext.Current.CancellationToken));

        var profileRepository = new ProfileRepository(fixture.Database);
        var preferencesRepository = new PreferencesRepository(fixture.Database);
        Assert.Null(await profileRepository.GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(await preferencesRepository.GetAsync(TestContext.Current.CancellationToken));

        await unitOfWork.ExecuteAsync(async (context, cancellationToken) =>
        {
            await context.Profiles.SaveAsync(new Profile("Commit", true), cancellationToken);
            await context.Preferences.SaveAsync(preferences, cancellationToken);
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Commit", (await profileRepository.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        Assert.Equal(preferences, await preferencesRepository.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pending_remote_envelopes_exclude_processed_message_ids()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var processed = new RemoteEnvelope("processed", [1], null, null, null, null, DateTimeOffset.UtcNow);
        var pending = new RemoteEnvelope("pending", [2], null, null, null, null, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(await repository.TryInsertAndMarkProcessedAsync(processed, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryInsertAsync(pending, TestContext.Current.CancellationToken));

        var result = await repository.ListPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["pending"], result.Select(envelope => envelope.MessageId));
    }

    [Fact]
    public async Task Processed_conflict_rolls_back_new_envelope_insert()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var messageId = "already-processed";
        await using (var connection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO processed_remote_messages (message_id, processed_utc) VALUES ($id, $processed);";
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$processed", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var envelope = new RemoteEnvelope(messageId, [9], null, null, null, null, DateTimeOffset.UtcNow);
        Assert.False(await repository.TryInsertAndMarkProcessedAsync(envelope, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Null(await repository.GetAsync(messageId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ambient_processed_conflict_rolls_back_new_envelope_before_outer_commit()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var messageId = "ambient-already-processed";
        await using (var connection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO processed_remote_messages (message_id, processed_utc) VALUES ($id, $processed);";
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$processed", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var envelope = new RemoteEnvelope(messageId, [9], null, null, null, null, DateTimeOffset.UtcNow);
        var unitOfWork = new AppUnitOfWork(fixture.Database);
        await unitOfWork.ExecuteAsync(async (context, cancellationToken) =>
        {
            Assert.False(await context.RemoteEnvelopes.TryInsertAndMarkProcessedAsync(
                envelope, DateTimeOffset.UtcNow, cancellationToken));
            // Deliberately continue so a leaked envelope would be committed here.
        }, TestContext.Current.CancellationToken);

        var repository = new RemoteEnvelopeRepository(fixture.Database);
        Assert.Null(await repository.GetAsync(messageId, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(messageId,
            (await repository.ListPendingAsync(TestContext.Current.CancellationToken))
            .Select(item => item.MessageId));
    }

    [Fact]
    public async Task Restore_replaces_database_and_removes_stale_wal_sidecars()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Before", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("After", true), TestContext.Current.CancellationToken);

        await using var activeConnection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using (var command = activeConnection.CreateCommand())
        {
            command.CommandText = "UPDATE profiles SET recipient_name = 'WAL' WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var walPath = fixture.Options.DatabasePath + "-wal";
        var shmPath = fixture.Options.DatabasePath + "-shm";
        Assert.True(File.Exists(walPath));
        Assert.True(File.Exists(shmPath));
        var restoreTask = fixture.Backups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);
        await Task.Yield();
        Assert.False(restoreTask.IsCompleted);
        await activeConnection.DisposeAsync();
        var result = await restoreTask;

        Assert.True(result.Restored);
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));
        Assert.Equal("Before", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
    }

    [Fact]
    public async Task Restore_waits_for_inflight_write_lease_before_replacing_database()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Before", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        await using var activeConnection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await activeConnection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = activeConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE profiles SET recipient_name = 'InFlight' WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var restoreTask = fixture.Backups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);
        await Task.Yield();
        Assert.False(restoreTask.IsCompleted);

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        await activeConnection.DisposeAsync();
        var result = await restoreTask;

        Assert.True(result.Restored);
        Assert.Equal("Before", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
    }

    [Fact]
    public async Task Replacement_failure_preserves_current_database_after_wal_checkpoint()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Current", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("Still current", true), TestContext.Current.CancellationToken);

        var failingBackups = new DatabaseBackupService(
            fixture.Options,
            static (_, _) => throw new IOException("injected replacement failure"));
        var result = await failingBackups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.RestoreFailed, result.Failure);
        Assert.Equal("Still current", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
    }

    [Fact]
    public async Task Public_connection_and_supplied_migration_paths_configure_pragmas()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var asyncConnection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await AssertPragmasAsync(asyncConnection, TestContext.Current.CancellationToken);

        var suppliedPath = Path.Combine(Path.GetDirectoryName(fixture.Options.DatabasePath)!, "supplied.db");
        var supplied = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = suppliedPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await supplied.OpenAsync(TestContext.Current.CancellationToken);
        await new MigrationRunner(new DatabaseOptions(suppliedPath)).RunAsync(supplied, TestContext.Current.CancellationToken);
        await AssertPragmasAsync(supplied, TestContext.Current.CancellationToken);
        await supplied.DisposeAsync();
    }

    [Fact]
    public void Database_exposes_async_only_connection_initialization()
    {
        Assert.Null(typeof(Database).GetMethod("CreateConnection", Type.EmptyTypes));
        Assert.NotNull(typeof(Database).GetMethod(nameof(Database.CreateConnectionAsync)));
    }

    [Fact]
    public async Task Concurrent_first_use_across_database_instances_awaits_one_completed_initialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = new Database(options);
            var secondDatabase = new Database(options);
            var connections = await Task.WhenAll(
                Enumerable.Range(0, 6).Select(_ => database.CreateConnectionAsync(TestContext.Current.CancellationToken))
                    .Concat(Enumerable.Range(0, 6).Select(_ => secondDatabase.CreateConnectionAsync(TestContext.Current.CancellationToken))));
            foreach (var connection in connections) await connection.DisposeAsync();
            var notes = await new LocalNoteRepository(database).ListEnabledAsync(TestContext.Current.CancellationToken);
            Assert.Equal(12, notes.Count);
            Assert.Equal(1, database.InitializationRunCount);
            Assert.Equal(1, secondDatabase.InitializationRunCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
        command.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), ignoreCase: true);
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private DatabaseFixture(string root, DatabaseOptions options, Database database, DatabaseBackupService backups)
        { Root = root; Options = options; Database = database; Backups = backups; }

        private string Root { get; }
        public DatabaseOptions Options { get; }
        public Database Database { get; }
        public DatabaseBackupService Backups { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options);
            return new DatabaseFixture(root, options, database, new DatabaseBackupService(options));
        }

        public async Task<IReadOnlyList<string>> ReadTableNamesAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.CreateConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            var names = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0)); return names;
        }

        public async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.CreateConnectionAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT version FROM schema_version WHERE id=1;"; return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
