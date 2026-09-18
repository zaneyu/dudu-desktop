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

    public static Task SeedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        SeedAsync(connection, transaction: null, cancellationToken);

    /// <summary>Seeds the bundled default notes exactly once per database, guarded by the
    /// <c>seed_state</c> watermark (see <c>Migrations/0007_seed_watermark.sql</c>). Re-running
    /// after a user deleted a default must not resurrect it; <c>ON CONFLICT DO NOTHING</c>
    /// alone cannot tell "fresh install" from "deliberately removed". Pass the caller's
    /// transaction to join an enclosing unit of work (e.g. the wipe-and-reseed maintenance
    /// transaction); otherwise a private transaction is used.</summary>
    internal static async Task SeedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var ownsTransaction = transaction is null;
        transaction ??= (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await IsSeededAsync(connection, transaction, cancellationToken))
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

                await using var watermark = connection.CreateCommand();
                watermark.Transaction = transaction;
                watermark.CommandText = """
                    INSERT INTO seed_state (id, version) VALUES (1, 1)
                    ON CONFLICT(id) DO UPDATE SET version = excluded.version;
                    """;
                await watermark.ExecuteNonQueryAsync(cancellationToken);
            }

            if (ownsTransaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            // A joined transaction belongs to the caller, which owns rollback.
            if (ownsTransaction)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private static async Task<bool> IsSeededAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM seed_state WHERE id = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }
}
