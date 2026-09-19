using Dudu.App.Notifications;
using Dudu.Infrastructure.Data;
using Xunit;

namespace Dudu.App.Tests.Notifications;

/// <summary>
/// Pre-handoff audit: <c>Database.LastRecoveryOutcome</c> had zero production consumers, so a
/// corrupt database that was silently restored from backup -- or silently started fresh with no
/// backup at all -- never told the user. These pin the notice content for each outcome and that
/// a healthy startup (<see cref="DatabaseRecoveryOutcome.None"/>) shows nothing.
/// </summary>
public sealed class DataRecoveryNoticeTests
{
    [Fact]
    public async Task Restored_from_backup_names_the_backup_and_warns_of_missing_recent_changes()
    {
        var sink = new RecordingNotificationSink();
        var service = new AppNotificationService(sink);

        await service.ShowDataRecoveryNoticeAsync(
            DatabaseRecoveryOutcome.RestoredFromBackup,
            TestContext.Current.CancellationToken);

        var notification = Assert.Single(sink.Items);
        Assert.Equal("Dudu restored your data", notification.Title);
        Assert.Contains("restored the most recent backup", notification.Body);
        Assert.Contains("might be missing", notification.Body);
    }

    [Fact]
    public async Task Started_fresh_names_the_quarantine_folder_and_no_usable_backup()
    {
        var sink = new RecordingNotificationSink();
        var service = new AppNotificationService(sink);

        await service.ShowDataRecoveryNoticeAsync(
            DatabaseRecoveryOutcome.StartedFresh,
            TestContext.Current.CancellationToken);

        var notification = Assert.Single(sink.Items);
        Assert.Equal("Dudu started fresh", notification.Title);
        Assert.Contains("no usable backup was found", notification.Body);
        Assert.Contains("backups quarantine folder", notification.Body);
    }

    [Fact]
    public async Task No_recovery_shows_nothing()
    {
        var sink = new RecordingNotificationSink();
        var service = new AppNotificationService(sink);

        await service.ShowDataRecoveryNoticeAsync(
            DatabaseRecoveryOutcome.None,
            TestContext.Current.CancellationToken);

        Assert.Empty(sink.Items);
    }

    [Fact]
    public void Notice_bodies_carry_no_exception_text()
    {
        // The notice content must never leak an exception message or type name -- it is a
        // best-effort user notice, not a diagnostic. The actual exception still reaches the
        // existing error reporter via Database.FailureReporter, wired separately.
        var forbidden = new[] { "Exception", "SqliteException", "0x" };
        var root = FindRepositoryDirectory("src", "Dudu.App", "Notifications");
        var contents = File.ReadAllText(Path.Combine(root, "AppNotificationService.cs"));
        var noticeStart = contents.IndexOf("ShowDataRecoveryNoticeAsync", StringComparison.Ordinal);
        var noticeEnd = contents.IndexOf("public Task ShowReminderAsync", StringComparison.Ordinal);
        Assert.True(noticeStart >= 0);
        Assert.True(noticeEnd > noticeStart);
        var noticeSource = contents[noticeStart..noticeEnd];
        foreach (var symbol in forbidden)
        {
            Assert.DoesNotContain(symbol, noticeSource, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryDirectory(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            relativePath.CopyTo(parts, 1);
            var path = Path.Combine(parts);
            if (Directory.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }
}
