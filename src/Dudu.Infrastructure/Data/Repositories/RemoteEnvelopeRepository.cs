using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class RemoteEnvelopeRepository : SqliteRepository, IRemoteEnvelopeRepository
{
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">Decides which scheduled envelopes are due for
    /// <see cref="ListPendingAsync"/>. Defaults to the system clock.</param>
    public RemoteEnvelopeRepository(Database database, TimeProvider? timeProvider = null) : base(database) { _timeProvider = timeProvider ?? TimeProvider.System; }
    internal RemoteEnvelopeRepository(Database database, SqliteTransactionContext context, TimeProvider? timeProvider = null) : base(database, context) { _timeProvider = timeProvider ?? TimeProvider.System; }
    public async Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" WHERE message_id=$id;"; Add(command,"$id",messageId); await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken)?Read(reader):null;
    }

    public async Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken)
    {
        // processed_remote_messages is relay-delivery deduplication state, not user-read state.
        // Received ciphertext must remain visible until the user saves/consumes it.
        // Envelopes delivered early (a skewed sender/relay clock) stay stored but hidden until
        // their deliver-after moment, so a scheduled note is never listed before it is due.
        var now=_timeProvider.GetUtcNow();
        var result=new List<RemoteEnvelope>(); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" ORDER BY received_utc;"; await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)){var envelope=Read(reader); if(!IsScheduledAfter(envelope.DeliverAfterUtc,now))result.Add(envelope);}
        return result;
    }

    public async Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); AddInsert(command,envelope,InsertSql); return await command.ExecuteNonQueryAsync(cancellationToken)==1;
    }

    public async Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT EXISTS(SELECT 1 FROM processed_remote_messages WHERE message_id=$id);"; Add(command,"$id",messageId); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken))!=0; }

    public async Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    { ValidateMessageId(messageId); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;"; Add(command,"$id",messageId); Add(command,"$processed",Utc(processedUtc)); return await command.ExecuteNonQueryAsync(cancellationToken)==1; }

    public async Task<bool> TryConsumeAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            return await ConsumeAsync(boundConnection, messageId, processedUtc, cancellationToken);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await ConsumeAsync(connection, messageId, processedUtc, cancellationToken, transaction))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    {
        Validate(envelope);
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var savepoint = boundConnection.CreateCommand();
            savepoint.CommandText = "SAVEPOINT remote_insert_and_mark;";
            await savepoint.ExecuteNonQueryAsync(cancellationToken);

            await using var insert = boundConnection.CreateCommand();
            AddInsert(insert, envelope, InsertSql);
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await using var processed = boundConnection.CreateCommand();
                processed.CommandText = "INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;";
                Add(processed,"$id",envelope.MessageId); Add(processed,"$processed",Utc(processedUtc));
                if (await processed.ExecuteNonQueryAsync(cancellationToken) == 1)
                {
                    await using var release = boundConnection.CreateCommand();
                    release.CommandText = "RELEASE SAVEPOINT remote_insert_and_mark;";
                    await release.ExecuteNonQueryAsync(cancellationToken);
                    return true;
                }

                await RollbackSavepointAsync(boundConnection, cancellationToken);
                return false;
            }
            catch
            {
                await RollbackSavepointAsync(boundConnection, CancellationToken.None);
                throw;
            }
        }

        await using var connection=await OpenAsync(cancellationToken); await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var insert=connection.CreateCommand(); insert.Transaction=transaction; AddInsert(insert,envelope,InsertSql); await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var processed=connection.CreateCommand(); processed.Transaction=transaction; processed.CommandText="INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;"; Add(processed,"$id",envelope.MessageId); Add(processed,"$processed",Utc(processedUtc));
            if(await processed.ExecuteNonQueryAsync(cancellationToken)!=1){await transaction.RollbackAsync(CancellationToken.None);return false;}
            await transaction.CommitAsync(cancellationToken); return true;
        }
        catch{await transaction.RollbackAsync(CancellationToken.None);throw;}
    }

    public async Task DeleteAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="DELETE FROM remote_envelopes WHERE message_id=$id;"; Add(command,"$id",messageId); await command.ExecuteNonQueryAsync(cancellationToken); }

    public async Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken)
    {
        if (retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        // An envelope is only prunable once both its receipt and its deliver-after moment are older
        // than the retention window: a future-scheduled note must survive even if it was received
        // long ago. Comparisons are lexicographic over ISO-8601 roundtrip ("O") strings, which order
        // chronologically when all values carry the same UTC offset — ReceivedUtc is normalized on
        // write, and DeliverAfterUtc arrives verbatim from the relay in the same format.
        var cutoff = Utc(utcNow - retention);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM remote_envelopes WHERE received_utc <= $cutoff AND (deliver_after_utc IS NULL OR deliver_after_utc <= $cutoff);";
        Add(command, "$cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True only for a parsable deliver-after later than <paramref name="now"/>. An
    /// unparsable value is treated as due, matching RemoteSyncService.IsDeliveryDeferred, so a
    /// malformed timestamp can never hide a note forever.</summary>
    private static bool IsScheduledAfter(string? deliverAfterUtc, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(deliverAfterUtc)
        && DateTimeOffset.TryParse(deliverAfterUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var deliverAfter)
        && deliverAfter > now;

    private const string Select="SELECT message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc,hkdf_salt,created_utc FROM remote_envelopes";
    private const string InsertSql="INSERT INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc,hkdf_salt,created_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received,$salt,$createdUtc) ON CONFLICT(message_id) DO NOTHING;";
    private static void Validate(RemoteEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateMessageId(envelope.MessageId);
        if (envelope.Ciphertext is null || envelope.Ciphertext.Length == 0)
        {
            throw new ArgumentException("Remote envelope ciphertext cannot be empty.", nameof(envelope));
        }
    }

    private static void ValidateMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (messageId.Length > 256) throw new ArgumentException("Remote message IDs cannot exceed 256 characters.", nameof(messageId));
    }
    private static void AddInsert(SqliteCommand c,RemoteEnvelope e,string sql){c.CommandText=sql;Add(c,"$id",e.MessageId);Add(c,"$ciphertext",e.Ciphertext);Add(c,"$key",e.EphemeralPublicKey);Add(c,"$nonce",e.Nonce);Add(c,"$tag",e.AuthenticationTag);Add(c,"$deliverAfter",e.DeliverAfterUtc);Add(c,"$received",Utc(e.ReceivedUtc));Add(c,"$salt",e.HkdfSalt);Add(c,"$createdUtc",e.CreatedUtc);}
    private static RemoteEnvelope Read(SqliteDataReader r)=>new(r.GetString(0),(byte[])r[1],r.IsDBNull(2)?null:(byte[])r[2],r.IsDBNull(3)?null:(byte[])r[3],r.IsDBNull(4)?null:(byte[])r[4],r.IsDBNull(5)?null:r.GetString(5),ReadUtc(r[6])){HkdfSalt=r.IsDBNull(7)?null:(byte[])r[7],CreatedUtc=r.IsDBNull(8)?null:r.GetString(8)};

    private static async Task RollbackSavepointAsync(SqliteConnectionLease connection, CancellationToken cancellationToken)
    {
        await using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK TO SAVEPOINT remote_insert_and_mark;";
        await rollback.ExecuteNonQueryAsync(cancellationToken);
        await using var release = connection.CreateCommand();
        release.CommandText = "RELEASE SAVEPOINT remote_insert_and_mark;";
        await release.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ConsumeAsync(
        SqliteConnectionLease connection,
        string messageId,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var mark = connection.CreateCommand();
        if (transaction is not null) mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;";
        Add(mark, "$id", messageId);
        Add(mark, "$processed", Utc(processedUtc));
        // The relay path marks the message before the user opens it. Consuming an envelope must
        // therefore succeed both when this call creates the deduplication row and when that row
        // already exists. The delete below remains the authoritative existence check.
        await mark.ExecuteNonQueryAsync(cancellationToken);

        await using var delete = connection.CreateCommand();
        if (transaction is not null) delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM remote_envelopes WHERE message_id=$id;";
        Add(delete, "$id", messageId);
        return await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
