using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

public sealed class SettingsWindowXamlTests
{
    [Fact]
    public void Startup_settings_shell_does_not_require_merged_static_style_resources()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Dudu.App",
            "Windows",
            "SettingsWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("Style=\"{StaticResource", xaml, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DuduDesktop.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found from the test output path.");
    }
}
