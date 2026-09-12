using Dudu.App.Animation;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Dudu.App.Hosting;

/// <summary>
/// The concrete application composition used after the single-instance gate.
/// UI callbacks are required inputs so production cannot silently discard a
/// tray action or activation.
/// </summary>
public sealed class CompanionUiActions
{
    public CompanionUiActions(
        Action openHome,
        Action<StartupSettingsService> openSettings,
        Action exit)
    {
        OpenHome = openHome ?? throw new ArgumentNullException(nameof(openHome));
        OpenSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public Action OpenHome { get; }

    public Action<StartupSettingsService> OpenSettings { get; }

    public Action Exit { get; }
}

public static class WindowsCompanionProductionComposition
{
    public static WindowsCompanionBootstrap CreateBootstrap(
        CompanionUiActions actions,
        IActivationTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return new WindowsCompanionBootstrap(
            cancellationToken => CreateRuntimeAsync(actions, cancellationToken),
            transport);
    }

    private static async Task<IPrimaryAppRuntime> CreateRuntimeAsync(
        CompanionUiActions actions,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows companion composition requires Windows.");
        }

        var paths = AppPaths.ForCurrentUser();
        var services = new ServiceCollection()
            .AddDuduInfrastructure(new DatabaseOptions(paths.Database, paths.Backups))
            .BuildServiceProvider();
        var host = new AppHost(services, paths);
        var composer = default(SkiaFrameComposer);
        var presenter = default(LayeredFramePresenter);
        StartupRegistrationService? startup = null;

        try
        {
            var preferences = services.GetRequiredService<Preferences>();
            var pet = services.GetRequiredService<PetStateMachine>();
            var manifestPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Packs",
                "fallback",
                "manifest.json");
            var pack = await AssetManifestLoader.LoadAsync(manifestPath, cancellationToken);
            var animation = pack.ResolveAnimation(
                "idle",
                DateOnly.FromDateTime(DateTime.Now),
                SeasonalDates.Empty);
            composer = new SkiaFrameComposer(pack);
            presenter = new LayeredFramePresenter();
            startup = new StartupRegistrationService();
            var startupSettings = new StartupSettingsService(
                startup,
                services.GetRequiredService<IPreferencesRepository>(),
                preferences);
            var pause = new PauseStateStore();

            var runtime = await WindowsCompanionRuntime.CreateAsync(
                host,
                presenter,
                new PetPlacement("MISSING", 0.8, 0.8, 1),
                animation.NominalSize,
                pet,
                preferences,
                pauseState: () => pause.GetEffective(DateTimeOffset.UtcNow),
                openHome: actions.OpenHome,
                trayCommandHandlerFactory: lifecycle =>
                    new CompanionCommandRouter(
                        lifecycle,
                        pause,
                        () => actions.OpenSettings(startupSettings),
                        actions.Exit).Handle,
                initializeOverlay: async overlay =>
                {
                    using var frame = composer.Compose(pack, animation, 0);
                    await overlay.PresentAsync(frame, cancellationToken);
                    overlay.Show();
                },
                cancellationToken: cancellationToken);
            return new ComposedPrimaryRuntime(runtime, composer, presenter, startup, services);
        }
        catch
        {
            startup?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            composer?.Dispose();
            presenter?.Dispose();
            await host.DisposeAsync();
            await services.DisposeAsync();
            throw;
        }
    }

    private sealed class ComposedPrimaryRuntime(
        WindowsCompanionRuntime runtime,
        SkiaFrameComposer composer,
        LayeredFramePresenter presenter,
        StartupRegistrationService startup,
        ServiceProvider services) : IPrimaryAppRuntime
    {
        public Task StartAsync(CancellationToken cancellationToken = default) =>
            runtime.StartAsync(cancellationToken);

        public Task ActivateAsync(
            AppActivation activation,
            CancellationToken cancellationToken = default) =>
            runtime.ActivateAsync(activation, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await runtime.DisposeAsync();
            await startup.DisposeAsync();
            composer.Dispose();
            presenter.Dispose();
            await services.DisposeAsync();
        }
    }
}
