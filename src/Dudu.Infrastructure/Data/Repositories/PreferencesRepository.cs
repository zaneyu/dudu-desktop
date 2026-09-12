using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class PreferencesRepository : SqliteRepository, IPreferencesRepository
{
    public PreferencesRepository(Database database) : base(database) { }
    internal PreferencesRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<Preferences?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT theme,quiet_hours_enabled,quiet_hours_start,quiet_hours_end,reduced_motion,local_note_daily_limit,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,hydration_reminders_enabled,break_reminders_enabled FROM preferences WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        return new((AppTheme)reader.GetInt32(0),new QuietHours(reader.GetInt32(1)!=0,ReadTime(reader[2]),ReadTime(reader[3])),reader.GetInt32(4)!=0,reader.GetInt32(5),reader.GetInt32(6)!=0,reader.GetInt32(7)!=0,reader.GetInt32(8)!=0,TimeSpan.FromTicks(reader.GetInt64(9)),reader.GetInt32(10)!=0,reader.GetInt32(11)!=0);
    }

    public async Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO preferences (id,theme,quiet_hours_enabled,quiet_hours_start,quiet_hours_end,reduced_motion,local_note_daily_limit,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,hydration_reminders_enabled,break_reminders_enabled)
            VALUES (1,$theme,$qhEnabled,$start,$end,$motion,$limit,$launch,$top,$fullscreen,$interval,$hydration,$breaks)
            ON CONFLICT(id) DO UPDATE SET theme=excluded.theme,quiet_hours_enabled=excluded.quiet_hours_enabled,quiet_hours_start=excluded.quiet_hours_start,quiet_hours_end=excluded.quiet_hours_end,reduced_motion=excluded.reduced_motion,local_note_daily_limit=excluded.local_note_daily_limit,launch_at_sign_in=excluded.launch_at_sign_in,always_on_top=excluded.always_on_top,hide_pet_during_fullscreen=excluded.hide_pet_during_fullscreen,ambient_minimum_interval_ticks=excluded.ambient_minimum_interval_ticks,hydration_reminders_enabled=excluded.hydration_reminders_enabled,break_reminders_enabled=excluded.break_reminders_enabled;
            """;
        Add(command,"$theme",(int)preferences.Theme); Add(command,"$qhEnabled",preferences.QuietHours.Enabled?1:0); Add(command,"$start",Time(preferences.QuietHours.Start)); Add(command,"$end",Time(preferences.QuietHours.End)); Add(command,"$motion",preferences.ReducedMotion?1:0); Add(command,"$limit",preferences.LocalNoteDailyLimit); Add(command,"$launch",preferences.LaunchAtSignIn?1:0); Add(command,"$top",preferences.AlwaysOnTop?1:0); Add(command,"$fullscreen",preferences.HidePetDuringFullscreen?1:0); Add(command,"$interval",preferences.AmbientMinimumInterval.Ticks); Add(command,"$hydration",preferences.HydrationRemindersEnabled?1:0); Add(command,"$breaks",preferences.BreakRemindersEnabled?1:0); await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
