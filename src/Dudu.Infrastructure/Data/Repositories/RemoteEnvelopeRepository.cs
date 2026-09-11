using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class RemoteEnvelopeRepository(Database database) : SqliteRepository(database), IRemoteEnvelopeRepository
{
    public async Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" WHERE message_id=$id;"; Add(command,"$id",messageId); await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken)?Read(reader):null;
    }

    public async Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var result=new List<RemoteEnvelope>(); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText=Select+" ORDER BY received_utc;"; await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken))result.Add(Read(reader)); return result;
    }

    public async Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); AddInsert(command,envelope,"INSERT OR IGNORE INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received);"); return await command.ExecuteNonQueryAsync(cancellationToken)==1;
    }

    public async Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT EXISTS(SELECT 1 FROM processed_remote_messages WHERE message_id=$id);"; Add(command,"$id",messageId); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken))!=0; }

    public async Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="INSERT OR IGNORE INTO processed_remote_messages (message_id,processed_utc) VALUES ($id,$processed);"; Add(command,"$id",messageId); Add(command,"$processed",Utc(processedUtc)); return await command.ExecuteNonQueryAsync(cancellationToken)==1; }

    public async Task<bool> TryInsertAndMarkProcessedAsync(RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken)
    {
        await using var connection=await OpenAsync(cancellationToken); await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var processed=connection.CreateCommand(); processed.Transaction=transaction; processed.CommandText="INSERT OR IGNORE INTO processed_remote_messages (message_id,processed_utc) VALUES ($id,$processed);"; Add(processed,"$id",envelope.MessageId); Add(processed,"$processed",Utc(processedUtc));
            if(await processed.ExecuteNonQueryAsync(cancellationToken)!=1){await transaction.RollbackAsync(CancellationToken.None);return false;}
            await using var insert=connection.CreateCommand(); insert.Transaction=transaction; AddInsert(insert,envelope,"INSERT OR IGNORE INTO remote_envelopes (message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc) VALUES ($id,$ciphertext,$key,$nonce,$tag,$deliverAfter,$received);"); await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken); return true;
        }
        catch{await transaction.RollbackAsync(CancellationToken.None);throw;}
    }

    public async Task DeleteAsync(string messageId, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="DELETE FROM remote_envelopes WHERE message_id=$id;"; Add(command,"$id",messageId); await command.ExecuteNonQueryAsync(cancellationToken); }

    private const string Select="SELECT message_id,ciphertext,ephemeral_public_key,nonce,authentication_tag,deliver_after_utc,received_utc FROM remote_envelopes";
    private static void AddInsert(SqliteCommand c,RemoteEnvelope e,string sql){c.CommandText=sql;Add(c,"$id",e.MessageId);Add(c,"$ciphertext",e.Ciphertext);Add(c,"$key",e.EphemeralPublicKey);Add(c,"$nonce",e.Nonce);Add(c,"$tag",e.AuthenticationTag);Add(c,"$deliverAfter",Utc(e.DeliverAfterUtc));Add(c,"$received",Utc(e.ReceivedUtc));}
    private static RemoteEnvelope Read(SqliteDataReader r)=>new(r.GetString(0),(byte[])r[1],r.IsDBNull(2)?null:(byte[])r[2],r.IsDBNull(3)?null:(byte[])r[3],r.IsDBNull(4)?null:(byte[])r[4],ReadNullableUtc(r[5]),ReadUtc(r[6]));
}
