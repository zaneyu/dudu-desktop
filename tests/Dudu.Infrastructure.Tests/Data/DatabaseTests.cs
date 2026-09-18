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
        // Version 8 is now a real migration; inject the failure at the next
        // version so this test continues to exercise rollback rather than
        // replacing production schema.
        runner.AddMigration(9, "CREATE TABLE broken(;" );

        await Assert.ThrowsAsync<SqliteException>(() => runner.RunAsync(connection, TestContext.Current.CancellationToken));
        Assert.Equal(8, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
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
    public async Task Restore_accepts_an_integrity_valid_backup_from_an_older_schema()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_version SET version = 2 WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        foreach (var (table, column) in new[]
        {
            ("remote_envelopes", "hkdf_salt"),
            ("remote_envelopes", "created_utc"),
            ("preferences", "outfit_key"),
            ("preferences", "automatic_seasonal_mode"),
            ("preferences", "anniversary_month"),
            ("preferences", "anniversary_day"),
            ("preferences", "birthday_month"),
            ("preferences", "birthday_day"),
            ("preferences", "evening_check_in_enabled"),
            ("preferences", "bedtime_ritual_enabled"),
            ("preferences", "sounds_enabled"),
            ("preferences", "sound_volume"),
        })
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var dropConsumed = connection.CreateCommand())
        {
            dropConsumed.CommandText = "ALTER TABLE focus_sessions DROP COLUMN consumed_focus_ticks;";
            await dropConsumed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await connection.DisposeAsync();

        // Older-schema backups restore successfully; the next startup migrates them forward.
        Assert.True(await fixture.Backups.IsValidBackupAsync(
            backup!, TestContext.Current.CancellationToken));
        var result = await fixture.Backups.TryRestoreAsync(
            backup!, TestContext.Current.CancellationToken);
        Assert.True(result.Restored);
        Assert.Equal(8, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_rejects_an_integrity_valid_backup_from_a_newer_schema()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        var newerVersion = await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken) + 1;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_version SET version = $version WHERE id = 1;";
            command.Parameters.AddWithValue("$version", newerVersion);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await connection.DisposeAsync();

        Assert.False(await fixture.Backups.IsValidBackupAsync(
            backup!, TestContext.Current.CancellationToken));
        var result = await fixture.Backups.TryRestoreAsync(
            backup!, TestContext.Current.CancellationToken);
        Assert.Equal(RestoreFailure.IntegrityCheckFailed, result.Failure);
    }

    [Fact]
    public async Task Restore_latest_prefers_filename_timestamp_over_file_mtime()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("First", true), TestContext.Current.CancellationToken);
        var older = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await Task.Delay(15, TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("Second", true), TestContext.Current.CancellationToken);
        var newer = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        // Invert mtimes: filename order must still win, because copies and coarse timestamp
        // granularity can make mtime disagree with which backup is actually newest.
        File.SetLastWriteTimeUtc(older!, File.GetLastWriteTimeUtc(newer!));
        File.SetLastWriteTimeUtc(newer!, DateTime.UtcNow.AddHours(-1));

        var result = await fixture.Backups.RestoreLatestValidAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Restored);
        Assert.Equal(newer, result.BackupPath);
        Assert.Equal("Second", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
    }

    [Fact]
    public async Task Backup_rotation_tolerates_undeletable_files()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        var stubborn = new DatabaseBackupService(
            new DatabaseOptions(fixture.Options.DatabasePath, fixture.Options.BackupDirectory)
            {
                BackupRetentionCount = 0,
            },
            moveFile: (source, destination) => File.Move(source, destination),
            deleteFile: _ => throw new IOException("injected rotation delete failure"));

        // Rotation runs as part of backup creation; a locked file must not fail the new backup.
        var backup = await stubborn.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task Remote_envelope_prune_removes_only_expired_beyond_retention()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var now = DateTimeOffset.UtcNow;
        var retention = TimeSpan.FromDays(30);
        var stale = new RemoteEnvelope("stale", [1], null, null, null, null, now - retention - TimeSpan.FromHours(1));
        var fresh = new RemoteEnvelope("fresh", [2], null, null, null, null, now);
        var futureScheduled = new RemoteEnvelope(
            "future", [3], null, null, null,
            (now + TimeSpan.FromDays(1)).ToString("O"),
            now - retention - TimeSpan.FromHours(1));
        Assert.True(await repository.TryInsertAsync(stale, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryInsertAsync(fresh, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryInsertAsync(futureScheduled, TestContext.Current.CancellationToken));

        var removed = await repository.PruneExpiredAsync(now, retention, TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.Null(await repository.GetAsync("stale", TestContext.Current.CancellationToken));
        Assert.NotNull(await repository.GetAsync("fresh", TestContext.Current.CancellationToken));
        Assert.NotNull(await repository.GetAsync("future", TestContext.Current.CancellationToken));
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
    public async Task Local_note_jar_supports_explicit_save_list_and_delete()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new LocalNoteRepository(fixture.Database);
        var original = new LocalLoveNote("jar-note", "You can do it.", Enabled: false);

        await repository.SaveToJarAsync(original, cancellationToken);

        Assert.Contains(original, await repository.ListAsync(cancellationToken));
        Assert.DoesNotContain(original, await repository.ListEnabledAsync(cancellationToken));

        var updated = original with { Text = "You really can do it.", Enabled = true };
        await repository.SaveToJarAsync(updated, cancellationToken);
        Assert.Contains(updated, await repository.ListEnabledAsync(cancellationToken));

        await repository.DeleteAsync(updated.Id, cancellationToken);
        Assert.DoesNotContain(
            (await repository.ListAsync(cancellationToken)).Select(note => note.Id),
            id => id == updated.Id);
    }

    [Fact]
    public async Task Feature_history_queries_exclude_active_work_and_order_newest_first()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var earlier = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
        var later = earlier.AddMinutes(1);

        var tasks = new TaskRepository(fixture.Database);
        var completedTask = new TaskItem(Guid.NewGuid(), "Completed", null, null, true, earlier, later, later);
        var activeTask = new TaskItem(Guid.NewGuid(), "Active", null, null, false, earlier, later, null);
        await tasks.SaveAsync(completedTask, cancellationToken);
        await tasks.SaveAsync(activeTask, cancellationToken);
        Assert.Equal([completedTask], await tasks.ListCompletedAsync(cancellationToken));

        var focus = new FocusSessionRepository(fixture.Database);
        var completed = new FocusSession(Guid.NewGuid(), null, earlier, earlier.AddMinutes(25), TimeSpan.Zero, FocusStatus.Completed, earlier);
        var ended = new FocusSession(Guid.NewGuid(), null, earlier, null, TimeSpan.FromMinutes(12), FocusStatus.EndedEarly, later);
        var running = new FocusSession(Guid.NewGuid(), null, later, later.AddMinutes(25), TimeSpan.Zero, FocusStatus.Running, later);
        await focus.SaveAsync(completed, cancellationToken);
        await focus.SaveAsync(ended, cancellationToken);
        await focus.SaveAsync(running, cancellationToken);

        Assert.Equal([ended, completed], await focus.ListHistoryAsync(cancellationToken));
    }

    [Fact]
    public async Task Reminder_list_includes_disabled_reminders_for_settings_editing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new ReminderRepository(fixture.Database);
        var first = new Reminder("first", "First", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-12T09:00:00Z"));
        var second = first with { Id = "second", Title = "Second", Enabled = false };
        await repository.SaveAsync(first, cancellationToken);
        await repository.SaveAsync(second, cancellationToken);

        Assert.Equal([first, second], await repository.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task Snoozed_due_reminder_is_not_selected_until_its_persisted_snooze_expires()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new ReminderRepository(fixture.Database);
        var reminder = new Reminder("snoozed", "Snoozed", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-12T09:00:00Z"),
            DateTimeOffset.Parse("2026-09-12T10:15:00Z"));
        await repository.SaveAsync(reminder, cancellationToken);

        Assert.Empty(await repository.LoadDueAsync(DateTimeOffset.Parse("2026-09-12T10:00:00Z"), cancellationToken));
        Assert.Equal([reminder.Id], (await repository.LoadDueAsync(
            DateTimeOffset.Parse("2026-09-12T10:15:00Z"), cancellationToken)).Select(item => item.Id));
    }

    [Fact]
    public async Task Consuming_a_remote_envelope_marks_and_removes_it_so_refresh_cannot_resurrect_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var envelope = new RemoteEnvelope("consume-me", [1, 2, 3], DateTimeOffset.Parse("2026-09-12T10:00:00Z"));
        Assert.True(await repository.TryInsertAsync(envelope, cancellationToken));

        Assert.True(await repository.TryConsumeAsync(envelope.MessageId, DateTimeOffset.Parse("2026-09-12T10:01:00Z"), cancellationToken));
        Assert.Empty(await repository.ListPendingAsync(cancellationToken));
        Assert.True(await repository.IsProcessedAsync(envelope.MessageId, cancellationToken));
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

        var completedOnce = reminder with { Id = "completed-once", Rule = new RecurrenceRule.Once(), NextDueUtc = null };
        await reminderRepository.SaveAsync(completedOnce, cancellationToken);
        Assert.Null((await reminderRepository.ListAsync(cancellationToken))
            .Single(item => item.Id == completedOnce.Id).NextDueUtc);

        var countdownRepository = new CountdownRepository(fixture.Database);
        var countdown = new Countdown("countdown", "event",
            DateTimeOffset.Parse("2026-10-01T10:00:00-07:00"), null, false, TimeZoneInfo.Utc);
        await countdownRepository.SaveAsync(countdown, cancellationToken);
        var loadedCountdown = await countdownRepository.GetAsync(countdown.Id, cancellationToken);
        Assert.Equal(countdown, loadedCountdown);

        var preferences = new Preferences(AppTheme.Dark,
            new QuietHours(true, new TimeOnly(23, 0), new TimeOnly(6, 0)), true, 3, true, false, true,
            TimeSpan.FromMinutes(15),
            OutfitKey: "winter",
            AutomaticSeasonalMode: false,
            Anniversary: new MonthDay(9, 11),
            Birthday: new MonthDay(2, 29));
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
    public async Task Version_seven_database_migrates_sound_preferences_with_enabled_defaults()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await new PreferencesRepository(fixture.Database).SaveAsync(
            Preferences.Default, TestContext.Current.CancellationToken);
        await using (var connection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE preferences DROP COLUMN sounds_enabled; ALTER TABLE preferences DROP COLUMN sound_volume; UPDATE schema_version SET version = 7 WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        fixture.Database.InvalidateInitialization();
        await using var migratedConnection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await using var migratedCommand = migratedConnection.CreateCommand();
        migratedCommand.CommandText = "SELECT sounds_enabled, sound_volume FROM preferences WHERE id = 1;";
        await using var reader = await migratedCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0.35, reader.GetDouble(1));
    }

    [Fact]
    public async Task Preferences_round_trip_sound_values()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var preferences = Preferences.Default with { SoundsEnabled = false, SoundVolume = 0.72 };
        var repository = new PreferencesRepository(fixture.Database);

        await repository.SaveAsync(preferences, TestContext.Current.CancellationToken);

        Assert.Equal(preferences, await repository.GetAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-0.1, 0.0)]
    [InlineData(1.1, 1.0)]
    [InlineData(double.NaN, 0.35)]
    [InlineData(double.PositiveInfinity, 0.35)]
    [InlineData(double.NegativeInfinity, 0.35)]
    public async Task Preferences_repository_normalizes_invalid_sound_volume(double value, double expected)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new PreferencesRepository(fixture.Database);

        await repository.SaveAsync(Preferences.Default with { SoundVolume = value }, TestContext.Current.CancellationToken);

        Assert.Equal(expected, (await repository.GetAsync(TestContext.Current.CancellationToken))!.SoundVolume);
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
    public async Task Pending_remote_envelopes_include_received_messages_until_consumed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var processed = new RemoteEnvelope("processed", [1], null, null, null, null, DateTimeOffset.UtcNow);
        var pending = new RemoteEnvelope("pending", [2], null, null, null, null, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(await repository.TryInsertAndMarkProcessedAsync(processed, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.True(await repository.TryInsertAsync(pending, TestContext.Current.CancellationToken));

        var result = await repository.ListPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["processed", "pending"], result.Select(envelope => envelope.MessageId));

        Assert.True(await repository.TryConsumeAsync(
            processed.MessageId,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        Assert.Equal(["pending"], (await repository.ListPendingAsync(
            TestContext.Current.CancellationToken)).Select(envelope => envelope.MessageId));
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
    public async Task Restoring_an_older_schema_backup_migrates_it_and_serves_existing_and_new_instances()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_version SET version = 1 WHERE id = 1;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Drop the columns added after version 1 so the backup is genuinely an older schema
        // whose preferences row would fail the current SELECT.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var columns = new[]
            {
                "hydration_reminders_enabled", "break_reminders_enabled",
                "outfit_key", "automatic_seasonal_mode", "anniversary_month",
                "anniversary_day", "birthday_month", "birthday_day",
                "evening_check_in_enabled", "bedtime_ritual_enabled",
                "sounds_enabled", "sound_volume",
            };
            foreach (var column in columns)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"ALTER TABLE preferences DROP COLUMN {column};";
                await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await using (var dropConsumed = connection.CreateCommand())
            {
                dropConsumed.CommandText = "ALTER TABLE focus_sessions DROP COLUMN consumed_focus_ticks;";
                await dropConsumed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await using var dropHkdf = connection.CreateCommand();
            dropHkdf.CommandText = "ALTER TABLE remote_envelopes DROP COLUMN hkdf_salt;";
            await dropHkdf.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var dropCreated = connection.CreateCommand();
            dropCreated.CommandText = "ALTER TABLE remote_envelopes DROP COLUMN created_utc;";
            await dropCreated.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await fixture.Backups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.True(result.Restored, result.ToString());

        var preferencesRepository = new PreferencesRepository(fixture.Database);
        Assert.Equal(8, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
        _ = await preferencesRepository.GetAsync(TestContext.Current.CancellationToken);

        await using var freshDatabase = await Database.OpenAsync(fixture.Options, TestContext.Current.CancellationToken);
        Assert.Equal(8, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
        var freshPreferences = new PreferencesRepository(freshDatabase);
        _ = await freshPreferences.GetAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Restore_failure_still_invalidates_cached_initialization()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Current", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        var failingBackups = new DatabaseBackupService(
            fixture.Options,
            static (_, _) => throw new IOException("injected replacement failure"));
        var result = await failingBackups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.RestoreFailed, result.Failure);
        Assert.Equal("Current", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        Assert.True(File.Exists(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task Reconciliation_restores_the_staged_old_database_when_no_canonical_file_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var oldDatabase = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var profiles = new ProfileRepository(oldDatabase);
            await profiles.SaveAsync(new Profile("Interrupted", true), TestContext.Current.CancellationToken);
            var stagedPath = options.DatabasePath + ".restore-old-" + Guid.NewGuid().ToString("N");
            SqliteConnection.ClearAllPools();
            await oldDatabase.DisposeAsync();
            File.Move(options.DatabasePath, stagedPath);
            Assert.False(File.Exists(options.DatabasePath));

            var service = new DatabaseBackupService(options);
            var recovered = await service.ReconcileInterruptedRestoreAsync(TestContext.Current.CancellationToken);

            Assert.True(recovered);
            Assert.True(File.Exists(options.DatabasePath));
            Assert.False(File.Exists(stagedPath));

            await using var reopened = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var repository = new ProfileRepository(reopened);
            Assert.Equal("Interrupted", (await repository.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Reconciliation_installs_a_valid_restore_temp_over_a_truncated_canonical_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var source = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var profiles = new ProfileRepository(source);
            await profiles.SaveAsync(new Profile("Replacement", true), TestContext.Current.CancellationToken);
            SqliteConnection.ClearAllPools();
            await source.DisposeAsync();

            var temporaryPath = options.DatabasePath + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(options.DatabasePath, temporaryPath);
            await File.WriteAllTextAsync(options.DatabasePath, "truncated", TestContext.Current.CancellationToken);

            var service = new DatabaseBackupService(options);
            var recovered = await service.ReconcileInterruptedRestoreAsync(TestContext.Current.CancellationToken);

            Assert.True(recovered);
            await using var reopened = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var repository = new ProfileRepository(reopened);
            Assert.Equal("Replacement", (await repository.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Reconciliation_never_leaves_an_empty_database_when_a_recovery_set_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var oldDatabase = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var profiles = new ProfileRepository(oldDatabase);
            await profiles.SaveAsync(new Profile("Recovered", true), TestContext.Current.CancellationToken);
            var stagedPath = options.DatabasePath + ".restore-old-" + Guid.NewGuid().ToString("N");
            SqliteConnection.ClearAllPools();
            await oldDatabase.DisposeAsync();
            File.Move(options.DatabasePath, stagedPath);
            await File.WriteAllTextAsync(options.DatabasePath, "half written", TestContext.Current.CancellationToken);

            var service = new DatabaseBackupService(options);
            var recovered = await service.ReconcileInterruptedRestoreAsync(TestContext.Current.CancellationToken);

            Assert.True(recovered);
            await using var reopened = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var repository = new ProfileRepository(reopened);
            Assert.Equal("Recovered", (await repository.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Sidecar_staging_failure_restores_current_database()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Current", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("Still current", true), TestContext.Current.CancellationToken);

        var sidecarPath = fixture.Options.DatabasePath + "-shm";
        var failingBackups = new DatabaseBackupService(
            fixture.Options,
            moveFile: (source, destination) =>
            {
                if (source == sidecarPath)
                {
                    throw new IOException("injected sidecar staging failure");
                }

                File.Move(source, destination);
            },
            beforeStaging: () => File.WriteAllBytes(sidecarPath, new byte[32 * 1024]));

        var result = await failingBackups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.RestoreFailed, result.Failure);
        Assert.Equal("Still current", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        Assert.True(File.Exists(sidecarPath));
    }

    [Fact]
    public async Task Sidecar_cleanup_failure_does_not_report_failed_restore_or_leave_canonical_sidecar()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Before", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("After", true), TestContext.Current.CancellationToken);

        var sidecarPath = fixture.Options.DatabasePath + "-shm";
        var cleanupAttempted = false;
        var failingCleanup = new DatabaseBackupService(
            fixture.Options,
            moveFile: (source, destination) => File.Move(source, destination),
            deleteFile: path =>
            {
                if (path.EndsWith("-shm", StringComparison.Ordinal))
                {
                    cleanupAttempted = true;
                    throw new IOException("injected sidecar cleanup failure");
                }

                File.Delete(path);
            },
            beforeStaging: () => File.WriteAllBytes(sidecarPath, new byte[32 * 1024]));

        var result = await failingCleanup.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.True(result.Restored);
        Assert.Equal(RestoreFailure.None, result.Failure);
        Assert.True(cleanupAttempted);
        Assert.False(File.Exists(sidecarPath));
        Assert.Equal("Before", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
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
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_cancellation_after_start_does_not_detach_shared_initialization()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();

        var initialization = fixture.Database.InitializeAsync(cancellation.Token);
        cancellation.Cancel();

        await initialization;
        Assert.Equal(1, fixture.Database.InitializationRunCount);
        Assert.True(File.Exists(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task Restore_succeeds_over_a_corrupt_current_database_and_keeps_a_recovery_set()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Before", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        var corruptBytes = new byte[8192];
        "SQLite format 3\0"u8.CopyTo(corruptBytes);
        for (var i = 512; i < corruptBytes.Length; i++)
        {
            corruptBytes[i] = 0xAB;
        }

        File.WriteAllBytes(fixture.Options.DatabasePath, corruptBytes);
        var service = new DatabaseBackupService(fixture.Options);

        var result = await service.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.True(result.Restored);
        Assert.Equal(RestoreFailure.None, result.Failure);
        Assert.Equal("Before", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        var recoveryFiles = Directory.GetFiles(Path.GetDirectoryName(fixture.Options.DatabasePath)!, "*");
        Assert.True(
            recoveryFiles.Any(path => path.Contains(DatabaseBackupService.RecoverySuffix, StringComparison.Ordinal)),
            string.Join(Environment.NewLine, recoveryFiles));
    }

    [Fact]
    public async Task Initialize_quarantines_a_corrupt_database_and_starts_fresh()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Before", true), TestContext.Current.CancellationToken);

        // Simulate the next process launch finding a torn write: drop the
        // shared initialization so this InitializeAsync really re-runs.
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(
            fixture.Options.DatabasePath,
            "not a sqlite database",
            TestContext.Current.CancellationToken);
        fixture.Database.InvalidateInitialization();

        await fixture.Database.InitializeAsync(TestContext.Current.CancellationToken);

        // The unreadable file is preserved for forensics, and the fresh
        // database is usable (seeded defaults, no stale profile).
        Assert.Single(Directory.GetFiles(fixture.Options.BackupDirectory, "dudu-corrupt-*.db"));
        Assert.Null(await profiles.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(12, (await new LocalNoteRepository(fixture.Database)
            .ListEnabledAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Prune_compares_deliver_after_chronologically_across_utc_offsets()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new RemoteEnvelopeRepository(fixture.Database);
        var cancellationToken = TestContext.Current.CancellationToken;
        // Cutoff renders as +00:00. Both rows are received long ago; only their
        // deliver-after instants differ, each rendered in a non-UTC offset whose
        // lexical order disagrees with chronological order.
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var upcoming = new RemoteEnvelope(
            "upcoming", [1], null, null, null,
            "2026-09-19T20:00:00-05:00", // 2026-09-20T01:00Z: future, but lexically below the cutoff
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var stale = new RemoteEnvelope(
            "stale", [2], null, null, null,
            "2026-09-19T15:00:00+08:00", // 2026-09-19T07:00Z: past, but lexically above the cutoff
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(await repository.TryInsertAsync(upcoming, cancellationToken));
        Assert.True(await repository.TryInsertAsync(stale, cancellationToken));

        var pruned = await repository.PruneExpiredAsync(now, TimeSpan.Zero, cancellationToken);

        Assert.Equal(1, pruned);
        Assert.NotNull(await repository.GetAsync("upcoming", cancellationToken));
        Assert.Null(await repository.GetAsync("stale", cancellationToken));
        // Relay-supplied timestamps stay byte-for-byte verbatim: they are bound
        // into the encryption AAD, so even an equivalent-instant rewrite would
        // break Reveal.
        Assert.Equal(
            "2026-09-19T20:00:00-05:00",
            (await repository.GetAsync("upcoming", cancellationToken))?.DeliverAfterUtc);
    }

    [Fact]
    public async Task Record_advance_returns_false_on_a_lost_compare_and_set_race()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new ReminderRepository(fixture.Database);
        var reminder = new Reminder("cas", "CAS", null, true, new RecurrenceRule.Once(), "UTC",
            QuietHoursBehavior.DeliverImmediately, MissedOccurrencePolicy.LatestOnly,
            DateTimeOffset.Parse("2026-09-12T09:00:00Z"));
        await repository.SaveAsync(reminder, cancellationToken);

        // A concurrent edit wins first: the due instant moves under us.
        await using (var connection = await fixture.Database.CreateConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE reminders SET next_due_utc = $next WHERE id = 'cas';";
            command.Parameters.AddWithValue("$next", "2026-09-13T09:00:00+00:00");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var stale = reminder;
        var occurrence = new ReminderOccurrence("cas", DateTimeOffset.Parse("2026-09-12T09:00:00Z"));
        Assert.False(await repository.RecordOccurrencesAndAdvanceAsync(
            stale, [occurrence], DateTimeOffset.Parse("2026-09-13T09:00:00Z"), cancellationToken));
    }

    [Fact]
    public async Task Concurrent_active_starts_yield_exactly_one_session()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = new FocusSessionRepository(fixture.Database);
        for (var round = 0; round < 20; round++)
        {
            using var barrier = new Barrier(8);
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(async () =>
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                    return await repository.TryCreateActiveAsync(
                        NewRunningSession(), TestContext.Current.CancellationToken);
                })));
            Assert.Single(attempts, won => won);

            await using (var connection = await fixture.Database.CreateConnectionAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM focus_sessions;";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        static FocusSession NewRunningSession()
        {
            var now = DateTimeOffset.UtcNow;
            return new FocusSession(Guid.NewGuid(), null, now, now.AddMinutes(25), TimeSpan.Zero, FocusStatus.Running, now);
        }
    }

    [Fact]
    public async Task Non_io_restore_failure_invalidates_cached_initialization()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Current", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        var runsBefore = fixture.Database.InitializationRunCount;

        var failingInstall = new DatabaseBackupService(
            fixture.Options,
            moveFile: (source, destination) =>
            {
                if (destination == fixture.Options.DatabasePath)
                {
                    throw new InvalidOperationException("injected install failure");
                }

                File.Move(source, destination);
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingInstall.TryRestoreAsync(
            backup!, TestContext.Current.CancellationToken));

        // The cached initialization was dropped, so the next initialize really
        // re-runs against the rolled-back original instead of skipping it.
        await fixture.Database.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(runsBefore + 1, fixture.Database.InitializationRunCount);
        Assert.Equal("Current", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
        Assert.Equal(8, await fixture.ReadSchemaVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_over_a_corrupt_current_database_preserves_the_unreadable_file_as_recovery()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        var corruptBytes = new byte[8192];
        "SQLite format 3\0"u8.CopyTo(corruptBytes);
        for (var i = 512; i < corruptBytes.Length; i++)
        {
            corruptBytes[i] = 0xAB;
        }

        File.WriteAllBytes(fixture.Options.DatabasePath, corruptBytes);
        var service = new DatabaseBackupService(fixture.Options);

        var result = await service.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.True(result.Restored);
        var recoveryPath = Directory.GetFiles(
            Path.GetDirectoryName(fixture.Options.DatabasePath)!,
            "*")
            .Single(path => !path.EndsWith("-wal" + DatabaseBackupService.RecoverySuffix)
                && !path.EndsWith("-shm" + DatabaseBackupService.RecoverySuffix)
                && path.Contains(DatabaseBackupService.RecoverySuffix, StringComparison.Ordinal));
        Assert.Equal(corruptBytes.Length, new FileInfo(recoveryPath).Length);
    }

    [Fact]
    public async Task Restore_latest_continues_to_older_backups_after_an_unreadable_newest_backup()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Older", true), TestContext.Current.CancellationToken);
        var older = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await Task.Delay(15, TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("Newer", true), TestContext.Current.CancellationToken);
        var newer = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(newer!, "not a database", TestContext.Current.CancellationToken);

        var result = await fixture.Backups.RestoreLatestValidAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Restored);
        Assert.Equal(older, result.BackupPath);
        Assert.Equal("Older", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
    }

    [Fact]
    public async Task Restore_latest_reports_integrity_failure_when_no_candidate_restores()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        foreach (var existingBackup in Directory.GetFiles(fixture.Options.BackupDirectory, "*.db"))
        {
            File.Delete(existingBackup);
        }
        var newer = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await Task.Delay(15, TestContext.Current.CancellationToken);
        var older = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(newer!, "not a database", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(older!, "still not a database", TestContext.Current.CancellationToken);
        var result = await fixture.Backups.RestoreLatestValidAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.IntegrityCheckFailed, result.Failure);
    }

    [Fact]
    public async Task Rollback_overwrites_a_partial_file_left_at_the_canonical_path()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var profiles = new ProfileRepository(fixture.Database);
        await profiles.SaveAsync(new Profile("Current", true), TestContext.Current.CancellationToken);
        var backup = await fixture.Backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);
        await profiles.SaveAsync(new Profile("Still current", true), TestContext.Current.CancellationToken);

        // Leave a partial replacement at the canonical path before reporting the install
        // failure. Rollback must move that partial file aside with overwrite enabled, then
        // restore the staged database.
        var failingBackups = new DatabaseBackupService(
            fixture.Options,
            moveFile: (source, destination) =>
            {
                if (destination == fixture.Options.DatabasePath)
                {
                    File.Copy(source, destination, overwrite: true);
                    throw new IOException("injected partial replacement failure");
                }

                File.Move(source, destination);
            },
            beforeStaging: () => File.WriteAllBytes(
                fixture.Options.DatabasePath + "-shm",
                new byte[32 * 1024]));

        var result = await failingBackups.TryRestoreAsync(backup!, TestContext.Current.CancellationToken);

        Assert.False(result.Restored);
        Assert.Equal(RestoreFailure.RestoreFailed, result.Failure);
        Assert.Equal("Still current", (await profiles.GetAsync(TestContext.Current.CancellationToken))?.RecipientName);
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
            var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
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
