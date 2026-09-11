using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class LocalNoteRepository(Database database) : SqliteRepository(database), ILocalNoteRepository
{
    public async Task<IReadOnlyList<LocalLoveNote>> ListEnabledAsync(CancellationToken cancellationToken)
    {
        var result = new List<LocalLoveNote>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,text,enabled FROM local_notes WHERE enabled=1 ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)!=0));
        return result;
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
}
