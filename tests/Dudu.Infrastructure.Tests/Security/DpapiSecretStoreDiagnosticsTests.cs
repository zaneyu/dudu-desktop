using System.Text;
using Dudu.Infrastructure.Security;
using Xunit;

namespace Dudu.Infrastructure.Tests.Security;

/// <summary>
/// P2 secret visibility: DPAPI secret read/write failures report their
/// <c>StartupFailureLogger</c> phase through the failure hook. Failures
/// still propagate exactly as before, so a failing secret write during
/// registration still leaves the device-id-last completion-marker ordering
/// intact. Absent reads and idempotent deletes are not failures and report
/// nothing. Windows DPAPI is required, like the existing store tests.
/// </summary>
public sealed class DpapiSecretStoreDiagnosticsTests
{
    [Fact]
    public async Task Secret_write_failure_reports_secret_write_and_still_throws()
    {
        RequireWindowsDpapi();
        using var root = new TemporaryRoot();
        // The secrets directory path is an existing file, so creating the
        // directory fails deterministically.
        var blocker = Path.Combine(root.Path, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.Current.CancellationToken);
        var store = new DpapiSecretStore(blocker);
        var reports = new List<(string Phase, Exception Exception)>();
        store.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        await Assert.ThrowsAnyAsync<Exception>(() => store.SetAsync(
            "relay-token",
            Encoding.UTF8.GetBytes("secret"),
            TestContext.Current.CancellationToken));

        var report = Assert.Single(reports);
        Assert.Equal(DpapiSecretStore.WriteFailurePhase, report.Phase);
        Assert.Equal("secret-write", report.Phase);
    }

    [Fact]
    public async Task Secret_read_failure_reports_secret_read_and_still_throws()
    {
        RequireWindowsDpapi();
        using var root = new TemporaryRoot();
        var store = new DpapiSecretStore(root.Path);
        Directory.CreateDirectory(root.Path);
        await File.WriteAllBytesAsync(
            Path.Combine(root.Path, "relay-token.bin"),
            [1, 2, 3],
            TestContext.Current.CancellationToken);
        var reports = new List<(string Phase, Exception Exception)>();
        store.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        await Assert.ThrowsAsync<SecretStoreException>(() => store.GetAsync(
            "relay-token",
            TestContext.Current.CancellationToken));

        var report = Assert.Single(reports);
        Assert.Equal(DpapiSecretStore.ReadFailurePhase, report.Phase);
        Assert.Equal("secret-read", report.Phase);
    }

    [Fact]
    public async Task Absent_read_and_idempotent_delete_report_nothing()
    {
        RequireWindowsDpapi();
        using var root = new TemporaryRoot();
        var store = new DpapiSecretStore(root.Path);
        var reports = new List<(string Phase, Exception Exception)>();
        store.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        Assert.Null(await store.GetAsync("missing", TestContext.Current.CancellationToken));
        await store.DeleteAsync("missing", TestContext.Current.CancellationToken);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task Successful_round_trip_reports_nothing()
    {
        RequireWindowsDpapi();
        using var root = new TemporaryRoot();
        var store = new DpapiSecretStore(root.Path);
        var reports = new List<(string Phase, Exception Exception)>();
        store.FailureReporter = (phase, exception) => reports.Add((phase, exception));

        await store.SetAsync(
            "relay-token",
            Encoding.UTF8.GetBytes("secret"),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            Encoding.UTF8.GetBytes("secret"),
            await store.GetAsync("relay-token", TestContext.Current.CancellationToken));

        Assert.Empty(reports);
    }

    private static void RequireWindowsDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows DPAPI required");
        }
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
        }
    }
}
