using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class ReminderRepository : SqliteRepository, IReminderRepository, IReminderWriter
{
    public ReminderRepository(Database database) : base(database) { }
    internal ReminderRepository(Database database, SqliteTransactionContext context) : base(database, context) { }

    public async Task<IReadOnlyList<Reminder>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<Reminder>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY next_due_utc, id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        var result = new List<Reminder>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE enabled = 1 AND next_due_utc <= $now AND (snoozed_until_utc IS NULL OR snoozed_until_utc <= $now) ORDER BY next_due_utc;";
        Add(command, "$now", Utc(utcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<bool> RecordOccurrencesAndAdvanceAsync(
        Reminder reminder,
        IReadOnlyList<ReminderOccurrence> occurrences,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentNullException.ThrowIfNull(occurrences);
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var boundUpdate = boundConnection.CreateCommand();
            AddCompareAndSet(boundUpdate, reminder, nextDueUtc);
            if (await boundUpdate.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return false;
            }

            foreach (var occurrence in occurrences)
            {
                await using var insert = boundConnection.CreateCommand();
                insert.CommandText = "INSERT INTO reminder_occurrences (reminder_id, due_utc) VALUES ($id, $due) ON CONFLICT(reminder_id, due_utc) DO NOTHING;";
                Add(insert, "$id", occurrence.ReminderId); Add(insert, "$due", Utc(occurrence.DueUtc));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return true;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            AddCompareAndSet(update, reminder, nextDueUtc);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            foreach (var occurrence in occurrences)
            {
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO reminder_occurrences (reminder_id, due_utc) VALUES ($id, $due) ON CONFLICT(reminder_id, due_utc) DO NOTHING;";
                Add(insert, "$id", occurrence.ReminderId); Add(insert, "$due", Utc(occurrence.DueUtc));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ReminderScheduler.ValidateForSave(reminder);
        var rule = EncodeRule(reminder.Rule);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reminders (id,title,details,enabled,rule_kind,local_time,weekdays_mask,interval_ticks,first_due_utc,
                local_time_zone_id,quiet_hours_behavior,missed_policy,next_due_utc,snoozed_until_utc,quiet_hours_enabled,quiet_hours_start,quiet_hours_end)
            VALUES ($id,$title,$details,$enabled,$kind,$localTime,$weekdays,$interval,$firstDue,$zone,$quietBehavior,$missed,$next,$snoozed,$qhEnabled,$qhStart,$qhEnd)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,details=excluded.details,enabled=excluded.enabled,rule_kind=excluded.rule_kind,
                local_time=excluded.local_time,weekdays_mask=excluded.weekdays_mask,interval_ticks=excluded.interval_ticks,first_due_utc=excluded.first_due_utc,
                local_time_zone_id=excluded.local_time_zone_id,quiet_hours_behavior=excluded.quiet_hours_behavior,missed_policy=excluded.missed_policy,
                next_due_utc=excluded.next_due_utc,snoozed_until_utc=excluded.snoozed_until_utc,quiet_hours_enabled=excluded.quiet_hours_enabled,
                quiet_hours_start=excluded.quiet_hours_start,quiet_hours_end=excluded.quiet_hours_end;
            """;
        Add(command,"$id",reminder.Id); Add(command,"$title",reminder.Title); Add(command,"$details",reminder.Details); Add(command,"$enabled",reminder.Enabled?1:0);
        Add(command,"$kind",rule.Kind); Add(command,"$localTime",rule.LocalTime); Add(command,"$weekdays",rule.Weekdays); Add(command,"$interval",rule.IntervalSeconds); Add(command,"$firstDue",rule.FirstDueUtc);
        Add(command,"$zone",reminder.LocalTimeZoneId); Add(command,"$quietBehavior",(int)reminder.QuietHoursBehavior); Add(command,"$missed",(int)reminder.MissedPolicy); Add(command,"$next",Utc(reminder.NextDueUtc)); Add(command,"$snoozed",Utc(reminder.SnoozedUntilUtc));
        Add(command,"$qhEnabled",reminder.QuietHours is null ? null : reminder.QuietHours.Enabled?1:0); Add(command,"$qhStart",reminder.QuietHours is null ? null : Time(reminder.QuietHours.Start)); Add(command,"$qhEnd",reminder.QuietHours is null ? null : Time(reminder.QuietHours.End));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string reminderId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // M3: a deleted reminder still sitting in PresentationCoordinator's held
        // queue (key "Reminder:<id>", see DurableNotification.Key) would
        // otherwise pop back up on the next restart even though it no longer
        // exists.
        command.CommandText = """
            DELETE FROM reminders WHERE id=$id;
            DELETE FROM held_presentations WHERE presentation_key='Reminder:' || $id;
            """;
        Add(command, "$id", reminderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Select = "SELECT id,title,details,enabled,rule_kind,local_time,weekdays_mask,interval_ticks,first_due_utc,local_time_zone_id,quiet_hours_behavior,missed_policy,next_due_utc,snoozed_until_utc,quiet_hours_enabled,quiet_hours_start,quiet_hours_end FROM reminders";
    private static Reminder Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), ReadString(r,2), r.GetInt32(3)!=0,
        DecodeRule(r.GetInt32(4), r[5], r[6], r[7], r[8]), r.GetString(9),
        (QuietHoursBehavior)r.GetInt32(10), (MissedOccurrencePolicy)r.GetInt32(11), ReadNullableUtc(r[12]), ReadNullableUtc(r[13]),
        ReadQuietHours(r[14], r[15], r[16]));

    private static void AddCompareAndSet(
        SqliteCommand command,
        Reminder expected,
        DateTimeOffset? nextDueUtc)
    {
        var rule = EncodeRule(expected.Rule);
        command.CommandText = """
            UPDATE reminders
            SET next_due_utc=$next, snoozed_until_utc=NULL
            WHERE id=$id AND title=$title AND details IS $details AND enabled=$enabled
              AND rule_kind=$kind AND local_time IS $localTime AND weekdays_mask IS $weekdays
              AND interval_ticks IS $interval AND first_due_utc IS $firstDue
              AND local_time_zone_id=$zone AND quiet_hours_behavior=$quietBehavior
              AND missed_policy=$missed AND next_due_utc IS $oldNext
              AND snoozed_until_utc IS $oldSnoozed AND quiet_hours_enabled IS $qhEnabled
              AND quiet_hours_start IS $qhStart AND quiet_hours_end IS $qhEnd;
            """;
        Add(command, "$id", expected.Id); Add(command, "$title", expected.Title); Add(command, "$details", expected.Details);
        Add(command, "$enabled", expected.Enabled ? 1 : 0); Add(command, "$kind", rule.Kind); Add(command, "$localTime", rule.LocalTime);
        Add(command, "$weekdays", rule.Weekdays); Add(command, "$interval", rule.IntervalSeconds); Add(command, "$firstDue", rule.FirstDueUtc);
        Add(command, "$zone", expected.LocalTimeZoneId); Add(command, "$quietBehavior", (int)expected.QuietHoursBehavior);
        Add(command, "$missed", (int)expected.MissedPolicy); Add(command, "$oldNext", Utc(expected.NextDueUtc));
        Add(command, "$oldSnoozed", Utc(expected.SnoozedUntilUtc)); Add(command, "$next", Utc(nextDueUtc));
        Add(command, "$qhEnabled", expected.QuietHours is null ? null : expected.QuietHours.Enabled ? 1 : 0);
        Add(command, "$qhStart", expected.QuietHours is null ? null : Time(expected.QuietHours.Start));
        Add(command, "$qhEnd", expected.QuietHours is null ? null : Time(expected.QuietHours.End));
    }
}
