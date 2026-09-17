using Dudu.App.Hosting;
using Xunit;

namespace Dudu.App.Tests.Hosting;

[Collection("DUDU_DATA_ROOT environment variable")]
public sealed class PackagedDataPathTests
{
    [Fact]
    public void Explicit_data_root_is_independent_of_package_install_location()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dudu-app-path-{Guid.NewGuid():N}");
        try
        {
            var paths = AppPaths.ForRoot(root);
            Assert.Equal(Path.Combine(root, "dudu.db"), paths.Database);
            Assert.Equal(Path.Combine(root, "backups"), paths.Backups);
            Assert.Equal(Path.Combine(root, "secrets"), paths.Secrets);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Current_user_root_honors_the_existing_data_root_override()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dudu-app-path-override-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("DUDU_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", root);

            var paths = AppPaths.ForCurrentUser();

            Assert.Equal(Path.GetFullPath(root), paths.Root);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "dudu.db"), paths.Database);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUDU_DATA_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
