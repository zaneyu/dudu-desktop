using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class FocusSessionRepository : SqliteRepository, IFocusSessionRepository
{
    public FocusSessionRepository(Database database) : base(database) { }
    internal FocusSessionRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<FocusSession?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = $id;"; Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<FocusSession?> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE status IN ($running, $paused) ORDER BY updated_utc DESC LIMIT 1;";
        Add(command, "$running", (int)FocusStatus.Running); Add(command, "$paused", (int)FocusStatus.Paused);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<FocusSession>> ListHistoryAsync(CancellationToken cancellationToken)
    {
        var result = new List<FocusSession>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE status NOT IN ($running, $paused) ORDER BY updated_utc DESC, id;";
        Add(command, "$running", (int)FocusStatus.Running);
        Add(command, "$paused", (int)FocusStatus.Paused);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<bool> TryCreateActiveAsync(FocusSession session, CancellationToken cancellationToken)
    {
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var boundCommand = boundConnection.CreateCommand();
            boundCommand.CommandText = """
                INSERT INTO focus_sessions (id, task_id, started_utc, ends_utc, remaining_when_paused_ticks, status, updated_utc)
                SELECT $id, $task, $started, $ends, $remaining, $status, $updated
                WHERE NOT EXISTS (SELECT 1 FROM focus_sessions WHERE status IN (0, 1));
                """;
            AddSession(boundCommand, session);
            return await boundCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO focus_sessions (id, task_id, started_utc, ends_utc, remaining_when_paused_ticks, status, updated_utc)
                SELECT $id, $task, $started, $ends, $remaining, $status, $updated
                WHERE NOT EXISTS (SELECT 1 FROM focus_sessions WHERE status IN (0, 1));
                """;
            AddSession(command, session); var count = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken); return count == 1;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<bool> TryCompareAndSetAsync(FocusSession expected, FocusSession replacement, CancellationToken cancellationToken)
    {
        if (expected.Id != replacement.Id) throw new ArgumentException("Focus compare-and-set requires the same session ID.", nameof(replacement));
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var boundCommand = boundConnection.CreateCommand();
            AddCompareAndSet(boundCommand, expected, replacement);
            return await boundCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                UPDATE focus_sessions SET task_id=$newTask, started_utc=$newStarted, ends_utc=$newEnds,
                    remaining_when_paused_ticks=$newRemaining, status=$newStatus, updated_utc=$newUpdated
                WHERE id=$id AND task_id IS $oldTask AND started_utc=$oldStarted AND ends_utc IS $oldEnds
                    AND remaining_when_paused_ticks=$oldRemaining AND status=$oldStatus AND updated_utc=$oldUpdated;
                """;
            Add(command, "$id", expected.Id.ToString("D")); Add(command, "$oldTask", expected.TaskId?.ToString("D"));
            Add(command, "$oldStarted", Utc(expected.StartedUtc)); Add(command, "$oldEnds", Utc(expected.EndsUtc));
            Add(command, "$oldRemaining", expected.RemainingWhenPaused.Ticks); Add(command, "$oldStatus", (int)expected.Status); Add(command, "$oldUpdated", Utc(expected.UpdatedUtc));
            Add(command, "$newTask", replacement.TaskId?.ToString("D")); Add(command, "$newStarted", Utc(replacement.StartedUtc)); Add(command, "$newEnds", Utc(replacement.EndsUtc));
            Add(command, "$newRemaining", replacement.RemainingWhenPaused.Ticks); Add(command, "$newStatus", (int)replacement.Status); Add(command, "$newUpdated", Utc(replacement.UpdatedUtc));
            var count = await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return count == 1;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task SaveAsync(FocusSession session, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO focus_sessions (id, task_id, started_utc, ends_utc, remaining_when_paused_ticks, status, updated_utc)
            VALUES ($id,$task,$started,$ends,$remaining,$status,$updated)
            ON CONFLICT(id) DO UPDATE SET task_id=excluded.task_id, started_utc=excluded.started_utc, ends_utc=excluded.ends_utc,
                remaining_when_paused_ticks=excluded.remaining_when_paused_ticks, status=excluded.status, updated_utc=excluded.updated_utc;
            """;
        AddSession(command, session); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Select = "SELECT id, task_id, started_utc, ends_utc, remaining_when_paused_ticks, status, updated_utc FROM focus_sessions";
    private static void AddCompareAndSet(SqliteCommand command, FocusSession expected, FocusSession replacement)
    {
        command.CommandText = """
            UPDATE focus_sessions SET task_id=$newTask, started_utc=$newStarted, ends_utc=$newEnds,
                remaining_when_paused_ticks=$newRemaining, status=$newStatus, updated_utc=$newUpdated
            WHERE id=$id AND task_id IS $oldTask AND started_utc=$oldStarted AND ends_utc IS $oldEnds
                AND remaining_when_paused_ticks=$oldRemaining AND status=$oldStatus AND updated_utc=$oldUpdated;
            """;
        Add(command, "$id", expected.Id.ToString("D")); Add(command, "$oldTask", expected.TaskId?.ToString("D"));
        Add(command, "$oldStarted", Utc(expected.StartedUtc)); Add(command, "$oldEnds", Utc(expected.EndsUtc));
        Add(command, "$oldRemaining", expected.RemainingWhenPaused.Ticks); Add(command, "$oldStatus", (int)expected.Status); Add(command, "$oldUpdated", Utc(expected.UpdatedUtc));
        Add(command, "$newTask", replacement.TaskId?.ToString("D")); Add(command, "$newStarted", Utc(replacement.StartedUtc)); Add(command, "$newEnds", Utc(replacement.EndsUtc));
        Add(command, "$newRemaining", replacement.RemainingWhenPaused.Ticks); Add(command, "$newStatus", (int)replacement.Status); Add(command, "$newUpdated", Utc(replacement.UpdatedUtc));
    }

    private static void AddSession(SqliteCommand c, FocusSession s)
    { Add(c,"$id",s.Id.ToString("D")); Add(c,"$task",s.TaskId?.ToString("D")); Add(c,"$started",Utc(s.StartedUtc)); Add(c,"$ends",Utc(s.EndsUtc)); Add(c,"$remaining",s.RemainingWhenPaused.Ticks); Add(c,"$status",(int)s.Status); Add(c,"$updated",Utc(s.UpdatedUtc)); }
    private static FocusSession Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)), ReadUtc(r[2]), ReadNullableUtc(r[3]), TimeSpan.FromTicks(r.GetInt64(4)), (FocusStatus)r.GetInt32(5), ReadUtc(r[6]));
}
