using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class HeldPresentationRepository : SqliteRepository, IHeldPresentationRepository
{
    public HeldPresentationRepository(Database database) : base(database) { }
    internal HeldPresentationRepository(Database database, SqliteTransactionContext context) : base(database, context) { }

    public async Task<IReadOnlyList<HeldPresentation>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<HeldPresentation>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT presentation_key,kind,item_id,title,body,animation_key,expires_utc,queued_utc,toasted FROM held_presentations ORDER BY queued_utc;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task SaveAsync(HeldPresentation item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO held_presentations
                (presentation_key,kind,item_id,title,body,animation_key,expires_utc,queued_utc,toasted)
            VALUES ($key,$kind,$id,$title,$body,$animationKey,$expires,$queued,$toasted)
            ON CONFLICT(presentation_key) DO UPDATE SET
                kind=excluded.kind,
                item_id=excluded.item_id,
                title=excluded.title,
                body=excluded.body,
                animation_key=excluded.animation_key,
                expires_utc=excluded.expires_utc,
                queued_utc=excluded.queued_utc,
                toasted=excluded.toasted;
            """;
        Add(command, "$key", item.Key);
        Add(command, "$kind", item.Kind);
        Add(command, "$id", item.Id);
        Add(command, "$title", item.Title);
        Add(command, "$body", item.Body);
        Add(command, "$animationKey", item.AnimationKey);
        Add(command, "$expires", Utc(item.ExpiresUtc));
        Add(command, "$queued", Utc(item.QueuedUtc));
        Add(command, "$toasted", item.Toasted ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM held_presentations WHERE presentation_key=$key;";
        Add(command, "$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkToastedAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE held_presentations SET toasted=1 WHERE presentation_key=$key;";
        Add(command, "$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static HeldPresentation Read(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        ReadString(r, 3),
        ReadString(r, 4),
        ReadString(r, 5),
        r.IsDBNull(6) ? null : ReadUtc(r.GetString(6)),
        ReadUtc(r.GetString(7)),
        r.GetInt32(8) != 0);
}
