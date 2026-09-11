using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data;

public static class SeedData
{
    private static readonly (string Id, string Text)[] Notes =
    [
        ("default-note-01", "You’ve got this 💛"),
        ("default-note-02", "I’m proud of you."),
        ("default-note-03", "Take a breath—I’m with you."),
        ("default-note-04", "A little water break for my favorite person?"),
        ("default-note-05", "One step at a time."),
        ("default-note-06", "You make ordinary days better."),
        ("default-note-07", "I hope something makes you smile today."),
        ("default-note-08", "Rest is productive too."),
        ("default-note-09", "You’re loved exactly as you are."),
        ("default-note-10", "Sending you a tiny hug."),
        ("default-note-11", "Your best is enough today."),
        ("default-note-12", "Can’t wait to see you."),
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
