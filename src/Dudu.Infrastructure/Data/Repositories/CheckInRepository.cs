using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class CheckInRepository(Database database) : SqliteRepository(database), ICheckInRepository
{
    public async Task SaveAsync(MoodCheckIn checkIn, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO mood_check_ins (id,choice,note,created_utc) VALUES ($id,$choice,$note,$created) ON CONFLICT(id) DO UPDATE SET choice=excluded.choice,note=excluded.note,created_utc=excluded.created_utc;";
        Add(command,"$id",checkIn.Id.ToString("D")); Add(command,"$choice",(int)checkIn.Choice); Add(command,"$note",checkIn.Note); Add(command,"$created",Utc(checkIn.CreatedUtc)); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MoodCheckIn>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var result = new List<MoodCheckIn>(); await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,choice,note,created_utc FROM mood_check_ins WHERE created_utc >= $since ORDER BY created_utc DESC;"; Add(command,"$since",Utc(sinceUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)),(MoodChoice)reader.GetInt32(1),ReadString(reader,2),ReadUtc(reader[3]))); return result;
    }
}
