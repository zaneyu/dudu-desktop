using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class ProfileRepository(Database database) : SqliteRepository(database), IProfileRepository
{
    public async Task<Profile?> GetAsync(CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT recipient_name,onboarding_complete FROM profiles WHERE id=1;"; await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken)?new(reader.GetString(0),reader.GetInt32(1)!=0):null; }

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="INSERT INTO profiles (id,recipient_name,onboarding_complete) VALUES (1,$name,$complete) ON CONFLICT(id) DO UPDATE SET recipient_name=excluded.recipient_name,onboarding_complete=excluded.onboarding_complete;"; Add(command,"$name",profile.RecipientName); Add(command,"$complete",profile.OnboardingComplete?1:0); await command.ExecuteNonQueryAsync(cancellationToken); }
}
