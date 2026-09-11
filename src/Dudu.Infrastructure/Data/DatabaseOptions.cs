namespace Dudu.Infrastructure.Data;

public sealed class DatabaseOptions
{
    public DatabaseOptions()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "dudu.db");
        BackupDirectory = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "backups");
    }

    public DatabaseOptions(string databasePath, string? backupDirectory = null)
    {
        DatabasePath = Path.GetFullPath(databasePath ?? throw new ArgumentNullException(nameof(databasePath)));
        BackupDirectory = Path.GetFullPath(backupDirectory ?? Path.Combine(
            Path.GetDirectoryName(DatabasePath) ?? Directory.GetCurrentDirectory(),
            "backups"));
    }

    public string DatabasePath { get; init; }

    public string BackupDirectory { get; init; }

    public int BusyTimeoutMilliseconds { get; init; } = 5000;

    public int BackupRetentionCount { get; init; } = 5;
}
