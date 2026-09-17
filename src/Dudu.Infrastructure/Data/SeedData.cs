using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public static class SeedData
{
    private static readonly (string Id, string Text)[] Notes =
    [
        ("default-note-01", "jiayo ada one small step"),
        ("default-note-02", "rest rest abit ok"),
        ("default-note-03", "so lihai de ada good job today"),
        ("default-note-04", "namnam break time"),
        ("default-note-05", "slow slow also ok"),
        ("default-note-06", "drink water ah"),
        ("default-note-07", "small progress counts too"),
        ("default-note-08", "time to shuijiaojiao"),
        ("default-note-09", "a little mwamwa from dudu"),
        ("default-note-10", "dudu peipei while u try"),
        ("default-note-11", "isok rest abit"),
        ("default-note-12", "one thing at a time ok"),
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
