using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class StartupCrashGuardTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dudu-crash-guard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Three_unclean_runs_put_the_next_run_in_safe_mode()
    {
        for (var run = 0; run < StartupCrashGuard.SafeModeThreshold; run++)
        {
            Assert.False(StartupCrashGuard.BeginRun(_directory).SafeMode);
        }

        var guard = StartupCrashGuard.BeginRun(_directory);

        Assert.True(guard.SafeMode);
        Assert.Equal(3, guard.ConsecutiveFailedRuns);
    }

    [Fact]
    public void Clean_run_resets_the_counter()
    {
        StartupCrashGuard.BeginRun(_directory);
        StartupCrashGuard.BeginRun(_directory);
        StartupCrashGuard.BeginRun(_directory).MarkCleanRun();

        var guard = StartupCrashGuard.BeginRun(_directory);

        Assert.False(guard.SafeMode);
        Assert.Equal(0, guard.ConsecutiveFailedRuns);
    }

    [Fact]
    public void Malformed_counter_is_treated_as_zero()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, StartupCrashGuard.FileName), "-7 garbage");

        Assert.Equal(0, StartupCrashGuard.BeginRun(_directory).ConsecutiveFailedRuns);
    }

    [Fact]
    public async Task Stable_period_marks_the_run_clean()
    {
        await StartupCrashGuard.BeginRun(_directory)
            .MarkCleanAfterAsync(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_directory, StartupCrashGuard.FileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
