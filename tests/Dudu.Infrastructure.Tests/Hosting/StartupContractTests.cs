using Xunit;

namespace Dudu.Infrastructure.Tests.Hosting;

public sealed class StartupContractTests
{
    [Fact]
    public void App_constructor_initializes_merged_application_resources_before_startup()
    {
        var root = FindRepositoryRoot();
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml"));
        var constructorStart = appCode.IndexOf("public App()", StringComparison.Ordinal);
        var constructorEnd = appCode.IndexOf("\n    }", constructorStart, StringComparison.Ordinal);

        Assert.True(constructorStart >= 0);
        Assert.True(constructorEnd > constructorStart);
        var constructor = appCode[constructorStart..constructorEnd];
        Assert.Contains("InitializeComponent();", constructor);
        Assert.Contains("Source=\"Themes/Colors.xaml\"", appXaml);
        Assert.Contains("Source=\"Themes/Controls.xaml\"", appXaml);
    }

    [Fact]
    public void Safe_mode_does_not_start_the_full_overlay_host()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));
        var safeRuntime = composition.IndexOf(
            "private sealed class SafeModePrimaryRuntime",
            StringComparison.Ordinal);

        Assert.True(safeRuntime >= 0);
        var body = composition[safeRuntime..];
        Assert.DoesNotContain("await host.StartAsync", body, StringComparison.Ordinal);
        Assert.Contains("await actions.OpenSettings", body, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
