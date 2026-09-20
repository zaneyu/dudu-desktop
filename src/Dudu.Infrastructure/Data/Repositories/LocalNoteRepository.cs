using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class LocalNoteRepository : SqliteRepository, ILocalNoteRepository
{
    public LocalNoteRepository(Database database) : base(database) { }
    internal LocalNoteRepository(Database database, SqliteTransactionContext context) : base(database, context) { }

    public async Task<IReadOnlyList<LocalLoveNote>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<LocalLoveNote>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,text,enabled FROM local_notes ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LocalLoveNote(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0));
        }

        return result;
    }

    public async Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        var result = new List<LocalLoveNote>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,text,enabled FROM local_notes WHERE enabled=1 ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)!=0));
        return result;
    }

    public async Task SaveToJarAsync(LocalLoveNote note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Text);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_notes (id,text,enabled,is_default)
            VALUES ($id,$text,$enabled,0)
            ON CONFLICT(id) DO UPDATE SET text=excluded.text, enabled=excluded.enabled;
            """;
        Add(command, "$id", note.Id);
        Add(command, "$text", note.Text);
        Add(command, "$enabled", note.Enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string noteId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // M3: a note deleted while a held_presentations row for it is still
        // sitting in PresentationCoordinator's held queue (key "LocalNote:<id>",
        // see DurableNotification.Key) would otherwise pop back up, full text
        // and all, on the next restart even though the note itself is gone.
        command.CommandText = """
            DELETE FROM local_notes WHERE id=$id;
            DELETE FROM held_presentations WHERE presentation_key='LocalNote:' || $id;
            """;
        Add(command, "$id", noteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountUnsolicitedShownAsync(DateOnly localDate, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_note_history WHERE local_date=$date AND unsolicited=1;"; Add(command,"$date",Date(localDate));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetMostRecentShownIdsAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return Array.Empty<string>();
        var result = new List<string>(); await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT note_id FROM local_note_history ORDER BY shown_utc DESC, id DESC LIMIT $count;"; Add(command,"$count",count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0)); return result;
    }

    public async Task<bool> TryRecordShownAsync(string noteId, DateTimeOffset shownUtc, DateOnly localDate, int dailyLimit, bool unsolicited, CancellationToken cancellationToken)
    {
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var boundCommand = boundConnection.CreateCommand();
            AddRecordCommand(boundCommand, noteId, shownUtc, localDate, dailyLimit, unsolicited);
            return await boundCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await using var connection = await OpenAsync(cancellationToken); await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO local_note_history (note_id, shown_utc, local_date, unsolicited)
                SELECT $id, $shown, $date, $unsolicited
                WHERE EXISTS (SELECT 1 FROM local_notes WHERE id=$id)
                  AND ($unsolicited=0 OR $limit > (SELECT COUNT(*) FROM local_note_history WHERE local_date=$date AND unsolicited=1));
                """;
            Add(command,"$id",noteId); Add(command,"$shown",Utc(shownUtc)); Add(command,"$date",Date(localDate)); Add(command,"$unsolicited",unsolicited?1:0); Add(command,"$limit",dailyLimit);
            var count = await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return count == 1;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    private static void AddRecordCommand(Microsoft.Data.Sqlite.SqliteCommand command, string noteId, DateTimeOffset shownUtc, DateOnly localDate, int dailyLimit, bool unsolicited)
    {
        command.CommandText = """
            INSERT INTO local_note_history (note_id, shown_utc, local_date, unsolicited)
            SELECT $id, $shown, $date, $unsolicited
            WHERE EXISTS (SELECT 1 FROM local_notes WHERE id=$id)
              AND ($unsolicited=0 OR $limit > (SELECT COUNT(*) FROM local_note_history WHERE local_date=$date AND unsolicited=1));
            """;
        Add(command,"$id",noteId); Add(command,"$shown",Utc(shownUtc)); Add(command,"$date",Date(localDate)); Add(command,"$unsolicited",unsolicited?1:0); Add(command,"$limit",dailyLimit);
    }
}
