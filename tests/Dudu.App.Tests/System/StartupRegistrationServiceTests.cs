using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public async Task Enable_is_atomic_current_user_only_and_idempotent()
    {
        var writer = new FakeWriter();
        await using var service = new StartupRegistrationService(
            "/opt/dudu/Dudu.exe",
            "/tmp/dudu-startup-" + Guid.NewGuid().ToString("N"),
            writer);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.SetEnabledAsync(true, cancellationToken);
        await service.SetEnabledAsync(true, cancellationToken);

        var entry = Assert.Single(writer.Writes);
        Assert.EndsWith(StartupRegistrationService.ShortcutFileName, entry.Path, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath("/opt/dudu/Dudu.exe"), entry.Target);
        Assert.Equal("--background", entry.Arguments);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public async Task Disable_removes_a_previously_enabled_shortcut_once()
    {
        var writer = new FakeWriter();
        await using var service = new StartupRegistrationService("/opt/Dudu.exe", "/tmp/startup", writer);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.SetEnabledAsync(true, cancellationToken);
        await service.SetEnabledAsync(false, cancellationToken);
        await service.SetEnabledAsync(false, cancellationToken);

        Assert.Single(writer.Deletes);
        Assert.False(service.IsEnabled);
    }

    private sealed class FakeWriter : IStartupLinkWriter
    {
        public List<(string Path, string Target, string Arguments)> Writes { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((shortcutPath, targetPath, arguments));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deletes.Add(shortcutPath);
            return Task.CompletedTask;
        }
    }
}
