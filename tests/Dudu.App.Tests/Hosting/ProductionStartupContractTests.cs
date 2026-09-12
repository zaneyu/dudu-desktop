using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class ProductionStartupContractTests
{
    [Fact]
    public void Production_overlay_visibility_uses_the_sampled_fullscreen_gate()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionBootstrap.cs"));

        Assert.DoesNotContain("overlay.Show();", composition);
        Assert.Contains("var fullscreen = new FullscreenDetector();", runtime);
        Assert.Contains("isFullscreen ??= fullscreen.IsForegroundFullscreen;", runtime);
        Assert.Contains("new WindowsCompanionEventSource(fullscreen)", runtime);
        Assert.Contains("await StartupVisibilityGate.ApplyAsync(", runtime);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output path.");
    }
}
