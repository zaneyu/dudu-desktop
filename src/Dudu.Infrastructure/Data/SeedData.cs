using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public static class SeedData
{
    private static readonly (string Id, string Text)[] Notes =
    [
        ("default-note-01", "u can de"),
        ("default-note-02", "rest rest abit ok"),
        ("default-note-03", "good job today lovuh"),
        ("default-note-04", "namnam properly ah"),
        ("default-note-05", "soon soon"),
        ("default-note-06", "drink water ah"),
        ("default-note-07", "proud of u"),
        ("default-note-08", "sleep early tonight ok"),
        ("default-note-09", "mwamwa"),
        ("default-note-10", "try try today lovuh"),
        ("default-note-11", "isok rest abit"),
        ("default-note-12", "busy is good today"),
    ];

    public static async Task SeedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var note in Notes)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_notes (id, text, enabled, is_default)
                    VALUES ($id, $text, 1, 1)
                    ON CONFLICT(id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$id", note.Id);
                command.Parameters.AddWithValue("$text", note.Text);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
