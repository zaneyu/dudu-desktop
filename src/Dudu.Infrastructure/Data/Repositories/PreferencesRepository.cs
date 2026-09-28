using Dudu.Core.Abstractions;
using Dudu.Core.Models;

namespace Dudu.Infrastructure.Data.Repositories;

public sealed class PreferencesRepository : SqliteRepository, IPreferencesRepository
{
    // Migration 0001 declares these columns NOT NULL without a default, so the
    // first INSERT must still supply them. The features they stored (quiet hours,
    // the local note daily limit) are gone: these fixed values are written only
    // when the row is created and are never read or updated afterwards, so an
    // upgraded install keeps whatever it had. Later retired columns (reminder
    // flags, outfit/seasonal dates, evening routines, global shortcut) all have
    // defaults or are nullable and are simply left alone. No column is dropped.
    internal const int LegacyQuietHoursEnabled = 0;
    internal const string LegacyQuietHoursStart = "22:00";
    internal const string LegacyQuietHoursEnd = "07:00";
    internal const int LegacyLocalNoteDailyLimit = 0;

    public PreferencesRepository(Database database) : base(database) { }
    internal PreferencesRepository(Database database, SqliteTransactionContext context) : base(database, context) { }

    public async Task<Preferences?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT theme,reduced_motion,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,sounds_enabled,sound_volume,pause_mode,pause_expires_utc
            FROM preferences WHERE id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            (AppTheme)reader.GetInt32(0),
            ReducedMotion: reader.GetInt32(1) != 0,
            LaunchAtSignIn: reader.GetInt32(2) != 0,
            AlwaysOnTop: reader.GetInt32(3) != 0,
            HidePetDuringFullscreen: reader.GetInt32(4) != 0,
            AmbientMinimumInterval: TimeSpan.FromTicks(reader.GetInt64(5)),
            SoundsEnabled: reader.GetInt32(6) != 0,
            SoundVolume: Preferences.ClampSoundVolume(reader.GetDouble(7)),
            PauseMode: reader.IsDBNull(8) ? null : reader.GetString(8),
            PauseExpiresUtc: ReadNullableUtc(reader[9]));
    }

    public async Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences (id,theme,quiet_hours_enabled,quiet_hours_start,quiet_hours_end,reduced_motion,local_note_daily_limit,launch_at_sign_in,always_on_top,hide_pet_during_fullscreen,ambient_minimum_interval_ticks,sounds_enabled,sound_volume,pause_mode,pause_expires_utc)
            VALUES (1,$theme,$legacyQuietHoursEnabled,$legacyQuietHoursStart,$legacyQuietHoursEnd,$motion,$legacyLocalNoteDailyLimit,$launch,$top,$fullscreen,$interval,$soundsEnabled,$soundVolume,$pauseMode,$pauseExpires)
            ON CONFLICT(id) DO UPDATE SET theme=excluded.theme,reduced_motion=excluded.reduced_motion,launch_at_sign_in=excluded.launch_at_sign_in,always_on_top=excluded.always_on_top,hide_pet_during_fullscreen=excluded.hide_pet_during_fullscreen,ambient_minimum_interval_ticks=excluded.ambient_minimum_interval_ticks,sounds_enabled=excluded.sounds_enabled,sound_volume=excluded.sound_volume,pause_mode=excluded.pause_mode,pause_expires_utc=excluded.pause_expires_utc;
            """;
        Add(command, "$theme", (int)preferences.Theme);
        Add(command, "$legacyQuietHoursEnabled", LegacyQuietHoursEnabled);
        Add(command, "$legacyQuietHoursStart", LegacyQuietHoursStart);
        Add(command, "$legacyQuietHoursEnd", LegacyQuietHoursEnd);
        Add(command, "$motion", preferences.ReducedMotion ? 1 : 0);
        Add(command, "$legacyLocalNoteDailyLimit", LegacyLocalNoteDailyLimit);
        Add(command, "$launch", preferences.LaunchAtSignIn ? 1 : 0);
        Add(command, "$top", preferences.AlwaysOnTop ? 1 : 0);
        Add(command, "$fullscreen", preferences.HidePetDuringFullscreen ? 1 : 0);
        Add(command, "$interval", preferences.AmbientMinimumInterval.Ticks);
        Add(command, "$soundsEnabled", preferences.SoundsEnabled ? 1 : 0);
        Add(command, "$soundVolume", Preferences.ClampSoundVolume(preferences.SoundVolume));
        Add(command, "$pauseMode", string.IsNullOrWhiteSpace(preferences.PauseMode) ? DBNull.Value : preferences.PauseMode);
        Add(command, "$pauseExpires", (object?)Utc(preferences.PauseExpiresUtc) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
