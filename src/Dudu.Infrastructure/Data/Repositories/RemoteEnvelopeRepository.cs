using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class RemoteEnvelopeRepository : SqliteRepository, IRemoteEnvelopeRepository
{
    public RemoteEnvelopeRepository(Database database) : base(database) { }
    internal RemoteEnvelopeRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" WHERE message_id=$id;"; Add(command,"$id",messageId); await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken)?Read(reader):null;
    }

    public async Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var result=new List<RemoteEnvelope>(); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" WHERE NOT EXISTS (SELECT 1 FROM processed_remote_messages p WHERE p.message_id = remote_envelopes.message_id) ORDER BY received_utc;"; await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken))result.Add(Read(reader)); return result;
    }

    public async Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken)
    {
        Validate(envelope);
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); AddInsert(command,envelope,"INSERT INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received) ON CONFLICT(message_id) DO NOTHING;"); return await command.ExecuteNonQueryAsync(cancellationToken)==1;
    }

    public async Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT EXISTS(SELECT 1 FROM processed_remote_messages WHERE message_id=$id);"; Add(command,"$id",messageId); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken))!=0; }

    public async Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    { ValidateMessageId(messageId); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;"; Add(command,"$id",messageId); Add(command,"$processed",Utc(processedUtc)); return await command.ExecuteNonQueryAsync(cancellationToken)==1; }

    public async Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    {
        Validate(envelope);
        if (IsTransactionBound)
        {
            await using var boundConnection = await OpenAsync(cancellationToken);
            await using var insert = boundConnection.CreateCommand();
            AddInsert(insert, envelope, "INSERT INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received) ON CONFLICT(message_id) DO NOTHING;");
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var processed = boundConnection.CreateCommand();
            processed.CommandText = "INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;";
            Add(processed,"$id",envelope.MessageId); Add(processed,"$processed",Utc(processedUtc));
            return await processed.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await using var connection=await OpenAsync(cancellationToken); await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var insert=connection.CreateCommand(); insert.Transaction=transaction; AddInsert(insert,envelope,"INSERT INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received) ON CONFLICT(message_id) DO NOTHING;"); await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var processed=connection.CreateCommand(); processed.Transaction=transaction; processed.CommandText="INSERT INTO processed_remote_messages (message_id,processed_utc) SELECT $id,$processed WHERE EXISTS (SELECT 1 FROM remote_envelopes WHERE message_id=$id) ON CONFLICT(message_id) DO NOTHING;"; Add(processed,"$id",envelope.MessageId); Add(processed,"$processed",Utc(processedUtc));
            if(await processed.ExecuteNonQueryAsync(cancellationToken)!=1){await transaction.RollbackAsync(CancellationToken.None);return false;}
            await transaction.CommitAsync(cancellationToken); return true;
        }
        catch{await transaction.RollbackAsync(CancellationToken.None);throw;}
    }

    public async Task DeleteAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="DELETE FROM remote_envelopes WHERE message_id=$id;"; Add(command,"$id",messageId); await command.ExecuteNonQueryAsync(cancellationToken); }

    private const string Select="SELECT message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc FROM remote_envelopes";
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
    private static void AddInsert(SqliteCommand c,RemoteEnvelope e,string sql){c.CommandText=sql;Add(c,"$id",e.MessageId);Add(c,"$ciphertext",e.Ciphertext);Add(c,"$key",e.EphemeralPublicKey);Add(c,"$nonce",e.Nonce);Add(c,"$tag",e.AuthenticationTag);Add(c,"$deliverAfter",Utc(e.DeliverAfterUtc));Add(c,"$received",Utc(e.ReceivedUtc));}
    private static RemoteEnvelope Read(SqliteDataReader r)=>new(r.GetString(0),(byte[])r[1],r.IsDBNull(2)?null:(byte[])r[2],r.IsDBNull(3)?null:(byte[])r[3],r.IsDBNull(4)?null:(byte[])r[4],ReadNullableUtc(r[5]),ReadUtc(r[6]));
}
