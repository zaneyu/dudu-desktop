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
        Action exit,
        Action<CompanionSettingsContext>? configureSettings = null)
    {
        OpenHome = openHome ?? throw new ArgumentNullException(nameof(openHome));
        OpenSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
        ConfigureSettings = configureSettings;
    }

    public Action OpenHome { get; }

    public Action<StartupSettingsService> OpenSettings { get; }

    public Action Exit { get; }

    public Action<CompanionSettingsContext>? ConfigureSettings { get; }
}

public sealed record CompanionSettingsContext(
    StartupSettingsService StartupSettings,
    StartupRegistrationService StartupRegistration,
    IPreferencesRepository Preferences,
    IProfileRepository Profiles,
    IPetPlacementRepository PetPlacements,
    IAppUnitOfWork UnitOfWork,
    IPairingService Pairing);

public sealed record CompanionLaunchOptions(bool Background)
{
    public static CompanionLaunchOptions Parse(string? arguments)
    {
        var background = (arguments ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => string.Equals(
                argument,
                "--background",
                StringComparison.OrdinalIgnoreCase));
        return new CompanionLaunchOptions(background);
    }

    /// <summary>
    /// LaunchAtSignIn is the persisted policy for a startup/background launch.
    /// A normal foreground launch always shows the companion; a background
    /// launch only does so when that persisted policy has been disabled.
    /// </summary>
    public bool ShouldShowOverlay(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return !Background || !preferences.LaunchAtSignIn;
    }
}

public static class WindowsCompanionProductionComposition
{
    public static WindowsCompanionBootstrap CreateBootstrap(
        CompanionUiActions actions,
        IActivationTransport? transport = null)
        => CreateBootstrap(string.Empty, actions, transport);

    public static WindowsCompanionBootstrap CreateBootstrap(
        string launchArguments,
        CompanionUiActions actions,
        IActivationTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return new WindowsCompanionBootstrap(
            cancellationToken => CreateRuntimeAsync(
                CompanionLaunchOptions.Parse(launchArguments),
                actions,
                cancellationToken),
            transport);
    }

    private static async Task<IPrimaryAppRuntime> CreateRuntimeAsync(
        CompanionLaunchOptions launchOptions,
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
            var database = services.GetRequiredService<Database>();
            await database.InitializeAsync(cancellationToken);
            var preferencesRepository = services.GetRequiredService<IPreferencesRepository>();
            var preferences = await preferencesRepository.GetAsync(cancellationToken)
                ?? services.GetRequiredService<Preferences>();
            var savedPlacements = await services
                .GetRequiredService<IPetPlacementRepository>()
                .ListAsync(cancellationToken);
            var initialPlacement = savedPlacements.FirstOrDefault()
                ?? new PetPlacement("MISSING", 0.8, 0.8, 1);
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
            await startup.SetEnabledAsync(preferences.LaunchAtSignIn, cancellationToken);
            var startupSettings = new StartupSettingsService(
                startup,
                preferencesRepository,
                preferences);
            actions.ConfigureSettings?.Invoke(new CompanionSettingsContext(
                startupSettings,
                startup,
                preferencesRepository,
                services.GetRequiredService<IProfileRepository>(),
                services.GetRequiredService<IPetPlacementRepository>(),
                services.GetRequiredService<IAppUnitOfWork>(),
                services.GetRequiredService<IPairingService>()));
            var pause = new PauseStateStore();

            var runtime = await WindowsCompanionRuntime.CreateAsync(
                host,
                presenter,
                initialPlacement,
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
                    if (launchOptions.ShouldShowOverlay(preferences))
                    {
                        overlay.Show();
                    }
                },
                initialUserVisible: launchOptions.ShouldShowOverlay(preferences),
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
