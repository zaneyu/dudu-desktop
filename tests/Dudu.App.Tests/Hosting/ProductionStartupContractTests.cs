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
        Assert.DoesNotContain("actionSurface.Open(", composition);
        Assert.Contains("await overlay.SetActionSurfaceAsync(actionSurface", composition);
        Assert.Contains("PresentOneShotAsync(petEvent, dismissalId, token)", composition);
        Assert.Contains("var fullscreen = new FullscreenDetector();", runtime);
        Assert.Contains("isFullscreen ??= fullscreen.IsForegroundFullscreen;", runtime);
        Assert.Contains("new WindowsCompanionEventSource(fullscreen)", runtime);
        Assert.Contains("await StartupVisibilityGate.ApplyAsync(", runtime);
    }

    [Fact]
    public void Native_overlay_clicks_and_settings_navigation_use_production_controllers()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Overlay", "OverlayWindowHost.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var dispatcher = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "System", "AwaitableUiDispatcher.cs"));

        Assert.Contains("ToggleFromPetBody(", host);
        Assert.Contains("OverlayActionSurfaceObserver.ObserveAsync", host);
        Assert.Contains("DispatcherQueue.GetForCurrentThread()", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue", app);
        Assert.Contains("_uiDispatcher.InvokeAsync", app);
        Assert.Contains("completion.TrySetException", dispatcher);
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
