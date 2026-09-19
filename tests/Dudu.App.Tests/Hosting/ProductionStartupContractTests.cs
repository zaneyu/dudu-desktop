using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class ProductionStartupContractTests
{
    [Fact]
    public void Safe_mode_returns_before_constructing_native_overlay_runtime()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        var safeModeBranch = composition.IndexOf("if (safeMode)", StringComparison.Ordinal);
        var nativeComposer = composition.IndexOf("new SkiaFrameComposer(pack)", StringComparison.Ordinal);
        var nativeRuntime = composition.IndexOf("WindowsCompanionRuntime.CreateAsync(", StringComparison.Ordinal);

        Assert.True(safeModeBranch >= 0);
        Assert.True(nativeComposer > safeModeBranch);
        Assert.True(nativeRuntime > safeModeBranch);
        Assert.Contains("new SafeModePrimaryRuntime", composition);
        Assert.DoesNotContain("AttachRemoteSync", composition[safeModeBranch..nativeRuntime]);
    }

    [Fact]
    public void Remote_note_arrival_sink_overrides_the_infrastructure_null_default()
    {
        // Dudu.Infrastructure.DependencyInjection registers NullRemoteNoteArrivalSink (and
        // NullReminderDueSink) as safe library-level defaults. Building the real production
        // ServiceProvider to assert what IRemoteNoteArrivalSink resolves to isn't practical
        // here: composition lives inline in a private method that goes on to touch WinUI and
        // the OS version, none of which this Mac/CI test host can run (see
        // Safe_mode_returns_before_constructing_native_overlay_runtime above for the same
        // constraint). So, like the other source-contract checks in this file, this asserts
        // directly on the composition source: the App composition root must override both
        // Null* defaults with their real implementations in the same builder chain, or a
        // regression here means notes/reminders silently stop reaching the screen again.
        var root = FindRepositoryRoot();
        var dependencyInjection = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.Infrastructure", "DependencyInjection.cs"));
        var composition = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs"));

        Assert.Contains(
            "services.AddSingleton<IRemoteNoteArrivalSink, NullRemoteNoteArrivalSink>();",
            dependencyInjection);
        Assert.Contains(
            "services.AddSingleton<IReminderDueSink, NullReminderDueSink>();",
            dependencyInjection);

        var reminderSinkIndex = composition.IndexOf(
            ".AddSingleton<IReminderDueSink>(provider => new ReminderDueSink(",
            StringComparison.Ordinal);
        var remoteNoteSinkIndex = composition.IndexOf(
            ".AddSingleton<IRemoteNoteArrivalSink>(provider => new RemoteNoteArrivalSink(",
            StringComparison.Ordinal);
        var buildServiceProviderIndex = composition.IndexOf(
            ".BuildServiceProvider();",
            StringComparison.Ordinal);

        Assert.True(reminderSinkIndex >= 0, "IReminderDueSink must be overridden with the real sink.");
        Assert.True(remoteNoteSinkIndex >= 0, "IRemoteNoteArrivalSink must be overridden with the real sink.");
        Assert.True(
            remoteNoteSinkIndex > reminderSinkIndex,
            "The remote-note sink override should sit next to the reminder sink override.");
        Assert.True(
            buildServiceProviderIndex > remoteNoteSinkIndex,
            "Both overrides must be registered before the service provider is built.");
    }

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
        Assert.Contains("new WindowsCompanionEventSource(fullscreen, errorReporter:", runtime);
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
        Assert.Contains("_actionDispatchQueue.Enqueue", host);
        Assert.DoesNotContain("OverlayActionSurfaceObserver.ObserveAsync", host);
        Assert.Contains("DispatcherQueue.GetForCurrentThread()", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue", app);
        Assert.Contains("_uiDispatcher.InvokeAsync", app);
        Assert.Contains("completion.TrySetException", dispatcher);
    }

    [Fact]
    public void Production_overlay_persists_dropped_drag_placements()
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

        Assert.Contains("persistPlacementAsync:", composition);
        Assert.Contains("placementRepository.SaveAsync", composition);
        Assert.Contains("persistPlacementAsync", runtime);
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
