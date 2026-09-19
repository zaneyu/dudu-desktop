using Dudu.Infrastructure.Data;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

public sealed class LocalDataMaintenanceTests
{
    [Fact]
    public async Task Delete_all_user_data_keeps_schema_and_defaults_but_removes_recoverable_copies()
    {
        var root = Path.Combine(Path.GetTempPath(), "dudu-maintenance-tests", Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(root, "backups");
        var secrets = Path.Combine(root, "secrets");
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(secrets);
        var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), backups);
        var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);

        try
        {
            await using (var connection = await database.CreateConnectionAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO profiles (id, recipient_name, onboarding_complete) VALUES (1, 'Mia', 1);
                    INSERT INTO local_notes (id, text, enabled, is_default) VALUES ('mine', 'Private note', 1, 0);
                    INSERT INTO local_note_history (note_id, shown_utc, local_date, unsolicited)
                    VALUES ('mine', '2026-09-12T10:00:00Z', '2026-09-12', 1);
                    INSERT INTO processed_remote_messages (message_id, processed_utc)
                    VALUES ('message-1', '2026-09-12T10:00:00Z');
                    INSERT INTO asset_packs (pack_id, version, manifest_path, private_use_only, selected)
                    VALUES ('custom', '1', 'C:\\pack\\manifest.json', 1, 1);
                    INSERT INTO held_presentations (presentation_key, kind, item_id, title, body, animation_key, expires_utc, queued_utc)
                    VALUES ('Reminder:mine', 'Reminder', 'mine', 'Stretch', NULL, NULL, NULL, '2026-09-12T10:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var backupPath = Path.Combine(backups, "recoverable.db");
            var secretPath = Path.Combine(secrets, "pairing.bin");
            var secretTempPath = Path.Combine(secrets, "pairing.bin.temporary.tmp");
            var restoreTempPath = options.DatabasePath + ".restore-abandoned";
            var restoreOldPath = options.DatabasePath + ".restore-old-abandoned";
            var restoreFailedPath = options.DatabasePath + ".restore-failed-abandoned";
            await File.WriteAllTextAsync(restoreTempPath, "temp", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(restoreOldPath, "old", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(restoreFailedPath, "failed", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(backupPath, "backup", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(secretPath, "secret", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(secretTempPath, "secret temp", TestContext.Current.CancellationToken);

            var service = new LocalDataMaintenanceService(options, database, secrets);
            await service.DeleteAllUserDataAsync(TestContext.Current.CancellationToken);

            await using var verify = await database.CreateConnectionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM profiles;"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM local_note_history;"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processed_remote_messages;"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM asset_packs;"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM held_presentations;"));
            Assert.Equal(12L, await ScalarAsync(verify, "SELECT COUNT(*) FROM local_notes WHERE is_default = 1;"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM local_notes WHERE is_default = 0;"));
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(secretPath));
            Assert.False(File.Exists(secretTempPath));
            Assert.False(File.Exists(restoreTempPath));
            Assert.False(File.Exists(restoreOldPath));
            Assert.False(File.Exists(restoreFailedPath));
        }
        finally
        {
            await database.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<long> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
