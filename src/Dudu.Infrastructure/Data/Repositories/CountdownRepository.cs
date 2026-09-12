using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class CountdownRepository : SqliteRepository, ICountdownRepository
{
    public CountdownRepository(Database database) : base(database) { }
    internal CountdownRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<Countdown?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = Select + " WHERE id=$id;"; Add(command,"$id",id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Countdown>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<Countdown>(); await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = Select + " ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader)); return result;
    }

    public async Task SaveAsync(Countdown countdown, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO countdowns (id,title,target_utc,target_date,is_all_day,local_time_zone_id) VALUES ($id,$title,$utc,$date,$allDay,$zone)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,target_utc=excluded.target_utc,target_date=excluded.target_date,is_all_day=excluded.is_all_day,local_time_zone_id=excluded.local_time_zone_id;
            """;
        Add(command,"$id",countdown.Id); Add(command,"$title",countdown.Title); Add(command,"$utc",Utc(countdown.TargetUtc)); Add(command,"$date",countdown.TargetDate is null ? null : Date(countdown.TargetDate.Value)); Add(command,"$allDay",countdown.IsAllDay?1:0); Add(command,"$zone",countdown.LocalTimeZone.Id); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    { await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText="DELETE FROM countdowns WHERE id=$id;"; Add(command,"$id",id); await command.ExecuteNonQueryAsync(cancellationToken); }

    private const string Select = "SELECT id,title,target_utc,target_date,is_all_day,local_time_zone_id FROM countdowns";
    private static Countdown Read(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var zoneId = r.GetString(5); TimeZoneInfo zone; try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); } catch (TimeZoneNotFoundException) when (zoneId == "UTC") { zone = TimeZoneInfo.Utc; }
        return new Countdown(r.GetString(0),r.GetString(1),ReadNullableUtc(r[2]),r.IsDBNull(3)?null:ReadDate(r[3]),r.GetInt32(4)!=0,zone);
    }
}
