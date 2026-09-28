using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

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

    /// <summary>Partner notes she revealed (saved as <c>remote-&lt;messageId&gt;</c>), most
    /// recently first saved first. <c>local_notes</c> has no timestamp column, so rowid
    /// order stands in for save order; the upsert in <see cref="SaveToJarAsync"/> keeps a
    /// row's rowid, so re-saving the same note keeps its place.</summary>
    public async Task<IReadOnlyList<LocalLoveNote>> ListRemoteAsync(CancellationToken cancellationToken)
    {
        var result = new List<LocalLoveNote>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // substr, not LIKE: LIKE treats '_' as a wildcard and is case-insensitive for ASCII.
        command.CommandText = "SELECT id,text,enabled FROM local_notes WHERE substr(id,1,7)='remote-' ORDER BY rowid DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LocalLoveNote(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0));
        }

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
        // M3: a note deleted while a held_presentations row for it is still
        // sitting in PresentationCoordinator's held queue (key "LocalNote:<id>",
        // see DurableNotification.Key) would otherwise pop back up, full text
        // and all, on the next restart even though the note itself is gone.
        //
        // Both DELETEs must commit or roll back together: Microsoft.Data.Sqlite
        // steps each statement in a multi-statement CommandText separately, so
        // outside any ambient transaction each one autocommits on its own --
        // a crash between them would leave the note gone but its held row
        // behind, reintroducing the "deleted note's text resurfaces" bug this
        // cascade exists to fix. When already running inside a caller's
        // transaction (IsTransactionBound), that transaction already covers
        // both statements and a second one is not started.
        var ownTransaction = IsTransactionBound ? null : await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            if (ownTransaction is not null)
            {
                command.Transaction = ownTransaction;
            }

            command.CommandText = """
                DELETE FROM local_notes WHERE id=$id;
                DELETE FROM held_presentations WHERE presentation_key='LocalNote:' || $id;
                """;
            Add(command, "$id", noteId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }
}
