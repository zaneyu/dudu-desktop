using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.Core.Abstractions;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
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
        // NullReminderDueSink) as safe library-level defaults; that registration itself is
        // asserted below by source, since it is a simple library-level default with no
        // reason to change. The override in the App composition root is instead asserted by
        // building a real ServiceCollection through AddProductionPresentationSinks (below):
        // a source-text check of the composition method here would still pass with the
        // override commented out or reordered, since the composition method itself can't be
        // invoked on this Mac/CI test host (it goes on to touch WinUI and the OS version).
        var root = FindRepositoryRoot();
        var dependencyInjection = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.Infrastructure", "DependencyInjection.cs"));

        Assert.Contains(
            "services.AddSingleton<IRemoteNoteArrivalSink, NullRemoteNoteArrivalSink>();",
            dependencyInjection);
        Assert.Contains(
            "services.AddSingleton<IReminderDueSink, NullReminderDueSink>();",
            dependencyInjection);
    }

    [Fact]
    public void Production_presentation_sinks_resolve_to_the_real_implementations()
    {
        // Unlike the source-text check above, this builds a real ServiceCollection and
        // resolves from it: it fails if AddProductionPresentationSinks is never called from
        // the composition root, is called before AddDuduInfrastructure (so the Null*
        // defaults would win instead), or is wired to the wrong interface -- regressions a
        // string search over the composition source could miss entirely.
        var testRoot = Path.Combine(Path.GetTempPath(), "dudu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var options = new DatabaseOptions(
                Path.Combine(testRoot, "dudu.db"),
                Path.Combine(testRoot, "backups"));
            using var provider = new ServiceCollection()
                .AddDuduInfrastructure(options)
                .AddProductionPresentationSinks(() => throw new InvalidOperationException(
                    "The presentation gateway is not ready."))
                .BuildServiceProvider();

            Assert.IsType<ReminderDueSink>(provider.GetRequiredService<IReminderDueSink>());
            Assert.IsType<RemoteNoteArrivalSink>(provider.GetRequiredService<IRemoteNoteArrivalSink>());
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); }
            catch (IOException) { }
        }
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
