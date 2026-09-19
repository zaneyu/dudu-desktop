using Dudu.Infrastructure.Data;
using Xunit;

namespace Dudu.Infrastructure.Tests.Data;

/// <summary>
/// P2 data visibility: database initialization, backup-prune, and
/// maintenance failures report their <c>StartupFailureLogger</c> phase
/// through the failure hooks. Every failure still propagates (or stays
/// best-effort) exactly as before — reporting is logging only.
/// </summary>
public sealed class DataFailureDiagnosticsTests
{
    [Fact]
    public async Task Database_initialization_failure_reports_db_init_and_still_throws()
    {
        using var root = new TemporaryRoot();
        // The database path's parent is a regular file, so creating the
        // database directory fails deterministically on every platform
        // without needing a real SQLite failure.
        var blocker = Path.Combine(root.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.Current.CancellationToken);
        var database = new Database(new DatabaseOptions(Path.Combine(blocker, "dudu.db")));
        var reports = new List<(string Phase, Exception Exception)>();
        database.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            database.InitializeAsync(TestContext.Current.CancellationToken));

        var report = Assert.Single(reports);
        Assert.Equal(Database.InitializationFailurePhase, report.Phase);
        Assert.Equal("db-init", report.Phase);
        Assert.Same(exception, report.Exception);
    }

    [Fact]
    public async Task Database_initialization_failure_reports_only_once_when_the_faulted_task_is_replayed()
    {
        // Review finding: DatabaseAccessCoordinator latches a genuine initialization failure and
        // hands the SAME faulted Task back to every subsequent InitializeAsync call (see
        // RunInitializationAsync's B2 comment), so every settings-page repository touch in safe
        // mode used to append a fresh startup-failure.log entry for the identical exception.
        using var root = new TemporaryRoot();
        var blocker = Path.Combine(root.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.Current.CancellationToken);
        var database = new Database(new DatabaseOptions(Path.Combine(blocker, "dudu.db")));
        var reports = new List<(string Phase, Exception Exception)>();
        database.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            database.InitializeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            database.InitializeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            database.InitializeAsync(TestContext.Current.CancellationToken));

        var report = Assert.Single(reports);
        Assert.Equal(Database.InitializationFailurePhase, report.Phase);
    }

    [Fact]
    public async Task Database_initialization_success_reports_nothing()
    {
        using var root = new TemporaryRoot();
        var database = new Database(new DatabaseOptions(Path.Combine(root.Path, "dudu.db")));
        var reports = new List<(string Phase, Exception Exception)>();
        database.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        await database.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task Backup_rotation_failure_reports_backup_prune_and_keeps_the_backup()
    {
        using var root = new TemporaryRoot();
        var options = new DatabaseOptions(
            Path.Combine(root.Path, "dudu.db"),
            Path.Combine(root.Path, "backups"))
        {
            BackupRetentionCount = 0,
        };
        await using var database = await Database.OpenAsync(
            options, TestContext.Current.CancellationToken);
        var backups = new DatabaseBackupService(
            options,
            moveFile: (source, destination) => File.Move(source, destination),
            deleteFile: _ => throw new IOException("rotation delete down"));
        var reports = new List<(string Phase, Exception Exception)>();
        backups.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        // Retention 0 forces rotation to attempt a delete, which fails. The
        // just-created backup must still succeed: rotation stays best-effort.
        var path = await backups.CreatePreMigrationBackupAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        var report = Assert.Single(reports);
        Assert.Equal(DatabaseBackupService.PruneFailurePhase, report.Phase);
        Assert.Equal("backup-prune", report.Phase);
        Assert.IsType<IOException>(report.Exception);
    }

    [Fact]
    public async Task Maintenance_sweep_failure_reports_backup_prune_and_still_throws()
    {
        using var root = new TemporaryRoot();
        var backupDirectory = Path.Combine(root.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var leftover = Path.Combine(backupDirectory, "leftover.db");
        await File.WriteAllTextAsync(leftover, "backup", TestContext.Current.CancellationToken);
        var options = new DatabaseOptions(
            Path.Combine(root.Path, "dudu.db"),
            backupDirectory);
        var database = new Database(options);
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("sweep delete down");
        var maintenance = new LocalDataMaintenanceService(
            options,
            database,
            Path.Combine(root.Path, "secrets"),
            deleteFile: _ => throw failure);
        var reports = new List<(string Phase, Exception Exception)>();
        maintenance.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            maintenance.DeleteAllUserDataAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        var report = Assert.Single(reports);
        Assert.Equal("backup-prune", report.Phase);
        Assert.Same(failure, report.Exception);
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dudu-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
