using System.Text;
using Dudu.Infrastructure.Security;
using Xunit;

namespace Dudu.Infrastructure.Tests.Security;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task Secret_round_trip_is_bound_to_current_user_and_not_plaintext()
    {
        RequireWindowsDpapi();
        using var directory = new TemporaryDirectory();
        var store = new DpapiSecretStore(directory.Path);
        var secret = Encoding.UTF8.GetBytes("desktop-capability-token");

        await store.SetAsync("relay-token", secret, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            "desktop-capability-token",
            await File.ReadAllTextAsync(
                Path.Combine(directory.Path, "relay-token.bin"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            secret,
            await store.GetAsync("relay-token", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_secret_returns_null_and_delete_is_idempotent()
    {
        RequireWindowsDpapi();
        using var directory = new TemporaryDirectory();
        var store = new DpapiSecretStore(directory.Path);

        Assert.Null(await store.GetAsync("missing", TestContext.Current.CancellationToken));
        await store.DeleteAsync("missing", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Corrupt_secret_raises_without_deleting_the_file()
    {
        RequireWindowsDpapi();
        using var directory = new TemporaryDirectory();
        var store = new DpapiSecretStore(directory.Path);
        var path = Path.Combine(directory.Path, "relay-token.bin");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecretStoreException>(
            () => store.GetAsync("relay-token", TestContext.Current.CancellationToken));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Invalid_secret_keys_are_rejected_before_file_access()
    {
        RequireWindowsDpapi();
        using var directory = new TemporaryDirectory();
        var store = new DpapiSecretStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteAsync("RelayToken"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteAsync("bad/key"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteAsync(new string('a', 65)));
    }

    private static void RequireWindowsDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows DPAPI required");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
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
