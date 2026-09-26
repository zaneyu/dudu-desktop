using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class PreferencesRepository : SqliteRepository, IPreferencesRepository
{
    public PreferencesRepository(Database database) : base(database) { }
    internal PreferencesRepository(Database database, SqliteTransactionContext context) : base(database, context) { }
    public async Task<Preferences?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT theme,quiet_hours_enabled,quiet_hours_start,quiet_hours_end,reduced_motion,local_note_daily_limit,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,hydration_reminders_enabled,break_reminders_enabled,outfit_key,automatic_seasonal_mode,anniversary_month,anniversary_day,birthday_month,birthday_day,evening_check_in_enabled,bedtime_ritual_enabled,sounds_enabled,sound_volume,global_shortcut,pause_mode,pause_expires_utc FROM preferences WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            (AppTheme)reader.GetInt32(0),
            new QuietHours(reader.GetInt32(1) != 0, ReadTime(reader[2]), ReadTime(reader[3])),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5),
            reader.GetInt32(6) != 0,
            reader.GetInt32(7) != 0,
            reader.GetInt32(8) != 0,
            TimeSpan.FromTicks(reader.GetInt64(9)),
            reader.GetInt32(10) != 0,
            reader.GetInt32(11) != 0,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetInt32(13) != 0,
            ReadMonthDay(reader, 14, 15),
            ReadMonthDay(reader, 16, 17),
            reader.GetInt32(18) != 0,
            reader.GetInt32(19) != 0,
            reader.GetInt32(20) != 0,
            Preferences.ClampSoundVolume(reader.GetDouble(21)),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            ReadNullableUtc(reader[24]));
    }

    public async Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = """
            INSERT INTO preferences (id,theme,quiet_hours_enabled,quiet_hours_start,quiet_hours_end,reduced_motion,local_note_daily_limit,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,hydration_reminders_enabled,break_reminders_enabled,outfit_key,automatic_seasonal_mode,anniversary_month,anniversary_day,birthday_month,birthday_day,evening_check_in_enabled,bedtime_ritual_enabled,sounds_enabled,sound_volume,global_shortcut,pause_mode,pause_expires_utc)
            VALUES (1,$theme,$qhEnabled,$start,$end,$motion,$limit,$launch,$top,$fullscreen,$interval,$hydration,$breaks,$outfit,$seasonal,$anniversaryMonth,$anniversaryDay,$birthdayMonth,$birthdayDay,$evening,$bedtime,$soundsEnabled,$soundVolume,$globalShortcut,$pauseMode,$pauseExpires)
            ON CONFLICT(id) DO UPDATE SET theme=excluded.theme,quiet_hours_enabled=excluded.quiet_hours_enabled,quiet_hours_start=excluded.quiet_hours_start,quiet_hours_end=excluded.quiet_hours_end,reduced_motion=excluded.reduced_motion,local_note_daily_limit=excluded.local_note_daily_limit,launch_at_sign_in=excluded.launch_at_sign_in,always_on_top=excluded.always_on_top,hide_pet_during_fullscreen=excluded.hide_pet_during_fullscreen,ambient_minimum_interval_ticks=excluded.ambient_minimum_interval_ticks,hydration_reminders_enabled=excluded.hydration_reminders_enabled,break_reminders_enabled=excluded.break_reminders_enabled,outfit_key=excluded.outfit_key,automatic_seasonal_mode=excluded.automatic_seasonal_mode,anniversary_month=excluded.anniversary_month,anniversary_day=excluded.anniversary_day,birthday_month=excluded.birthday_month,birthday_day=excluded.birthday_day,evening_check_in_enabled=excluded.evening_check_in_enabled,bedtime_ritual_enabled=excluded.bedtime_ritual_enabled,sounds_enabled=excluded.sounds_enabled,sound_volume=excluded.sound_volume,global_shortcut=excluded.global_shortcut,pause_mode=excluded.pause_mode,pause_expires_utc=excluded.pause_expires_utc;
            """;
        Add(command,"$theme",(int)preferences.Theme); Add(command,"$qhEnabled",preferences.QuietHours.Enabled?1:0); Add(command,"$start",Time(preferences.QuietHours.Start)); Add(command,"$end",Time(preferences.QuietHours.End)); Add(command,"$motion",preferences.ReducedMotion?1:0); Add(command,"$limit",preferences.LocalNoteDailyLimit); Add(command,"$launch",preferences.LaunchAtSignIn?1:0); Add(command,"$top",preferences.AlwaysOnTop?1:0); Add(command,"$fullscreen",preferences.HidePetDuringFullscreen?1:0); Add(command,"$interval",preferences.AmbientMinimumInterval.Ticks); Add(command,"$hydration",preferences.HydrationRemindersEnabled?1:0); Add(command,"$breaks",preferences.BreakRemindersEnabled?1:0); Add(command,"$outfit",(object?)preferences.OutfitKey ?? DBNull.Value); Add(command,"$seasonal",preferences.AutomaticSeasonalMode?1:0); AddMonthDay(command,"$anniversaryMonth","$anniversaryDay",preferences.Anniversary); AddMonthDay(command,"$birthdayMonth","$birthdayDay",preferences.Birthday); Add(command,"$evening",preferences.EveningCheckInEnabled?1:0); Add(command,"$bedtime",preferences.BedtimeRitualEnabled?1:0); Add(command,"$soundsEnabled",preferences.SoundsEnabled?1:0); Add(command,"$soundVolume",Preferences.ClampSoundVolume(preferences.SoundVolume)); Add(command,"$globalShortcut",string.IsNullOrWhiteSpace(preferences.GlobalShortcut) ? DBNull.Value : preferences.GlobalShortcut.Trim()); Add(command,"$pauseMode",string.IsNullOrWhiteSpace(preferences.PauseMode) ? DBNull.Value : preferences.PauseMode); Add(command,"$pauseExpires",(object?)Utc(preferences.PauseExpiresUtc) ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MonthDay? ReadMonthDay(SqliteDataReader reader, int monthIndex, int dayIndex)
    {
        if (reader.IsDBNull(monthIndex) || reader.IsDBNull(dayIndex)) return null;
        return new MonthDay(reader.GetInt32(monthIndex), reader.GetInt32(dayIndex));
    }

    private static void AddMonthDay(
        SqliteCommand command,
        string monthParameter,
        string dayParameter,
        MonthDay? value)
    {
        Add(command, monthParameter, value?.Month is int month ? month : DBNull.Value);
        Add(command, dayParameter, value?.Day is int day ? day : DBNull.Value);
    }
}
