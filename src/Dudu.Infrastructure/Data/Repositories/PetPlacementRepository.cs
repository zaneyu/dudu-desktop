using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class PetPlacementRepository : SqliteRepository, IPetPlacementRepository
{
    public PetPlacementRepository(Database database) : base(database) { }
    internal PetPlacementRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT monitor_device_name,normalized_x,normalized_y,scale FROM pet_placements WHERE monitor_device_name=$name;"; Add(command,"$name",monitorDeviceName); await using var reader=await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken)?Read(reader):null; }

    public async Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken)
    { var result=new List<PetPlacement>(); await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="SELECT monitor_device_name,normalized_x,normalized_y,scale FROM pet_placements ORDER BY monitor_device_name;"; await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken))result.Add(Read(reader)); return result; }

    public async Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="INSERT INTO pet_placements (monitor_device_name,normalized_x,normalized_y,scale) VALUES ($name,$x,$y,$scale) ON CONFLICT(monitor_device_name) DO UPDATE SET normalized_x=excluded.normalized_x,normalized_y=excluded.normalized_y,scale=excluded.scale;"; Add(command,"$name",placement.MonitorDeviceName); Add(command,"$x",placement.NormalizedX); Add(command,"$y",placement.NormalizedY); Add(command,"$scale",placement.Scale); await command.ExecuteNonQueryAsync(cancellationToken); }

    public async Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken)
    { await using var connection=await OpenAsync(cancellationToken); await using var command=connection.CreateCommand(); command.CommandText="DELETE FROM pet_placements WHERE monitor_device_name=$name;"; Add(command,"$name",monitorDeviceName); await command.ExecuteNonQueryAsync(cancellationToken); }

    private static PetPlacement Read(Microsoft.Data.Sqlite.SqliteDataReader r)=>new(r.GetString(0),r.GetDouble(1),r.GetDouble(2),r.GetDouble(3));
}
