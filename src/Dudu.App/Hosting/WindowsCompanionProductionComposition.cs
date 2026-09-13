using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Policies;
using Dudu.Core.Time;
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
        Func<CancellationToken, Task> openHome,
        Func<StartupSettingsService, CancellationToken, Task> openSettings,
        Func<CancellationToken, Task> exit,
        Action<CompanionSettingsContext>? configureSettings = null,
        Func<string, CancellationToken, Task>? navigateSettingsDestination = null)
    {
        OpenHome = openHome ?? throw new ArgumentNullException(nameof(openHome));
        OpenSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
        ConfigureSettings = configureSettings;
        NavigateSettingsDestination = navigateSettingsDestination;
    }

    public Func<CancellationToken, Task> OpenHome { get; }

    public Func<StartupSettingsService, CancellationToken, Task> OpenSettings { get; }

    public Func<CancellationToken, Task> Exit { get; }

    public Action<CompanionSettingsContext>? ConfigureSettings { get; }
    public Func<string, CancellationToken, Task>? NavigateSettingsDestination { get; }
}

public sealed record CompanionSettingsContext(
    StartupSettingsService StartupSettings,
    StartupRegistrationService StartupRegistration,
    PreferenceMutationCoordinator PreferenceMutations,
    IProfileRepository Profiles,
    IPetPlacementRepository PetPlacements,
    IAppUnitOfWork UnitOfWork,
    IPairingService Pairing,
    Profile? Profile,
    PetPlacement InitialPlacement,
    MonitorPlacementSnapshot PlacementSnapshot,
    Func<Preferences, PetPlacement, CancellationToken, Task> ApplyRuntimeAsync,
    Func<CancellationToken, Task<MonitorPlacementSnapshot>> CapturePlacementAsync,
    Func<PetPlacement, CancellationToken, Task> ApplyPlacementAsync,
    Func<bool, CancellationToken, Task> SetUserVisibleAsync)
{
    public CompanionFeatureContext? Features { get; init; }
    public OverlayActionSurfaceController? ActionSurface { get; init; }
    public OverlayCommandRouter? OverlayCommands { get; init; }
}

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

    public bool ShouldOpenSettings(Profile? profile) =>
        profile?.OnboardingComplete != true || !Background;

    public bool ShouldShowOverlay(Preferences preferences, Profile? profile) =>
        profile?.OnboardingComplete == true && ShouldShowOverlay(preferences);
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
        AnimationEngine? animationEngine = null;
        PetPresentationCoordinator? presentationCoordinator = null;
        StartupRegistrationService? startup = null;
        var actionSurface = new OverlayActionSurfaceController();

        try
        {
            var database = services.GetRequiredService<Database>();
            await database.InitializeAsync(cancellationToken);
            var preferencesRepository = services.GetRequiredService<IPreferencesRepository>();
            var preferences = await preferencesRepository.GetAsync(cancellationToken)
                ?? services.GetRequiredService<Preferences>();
            var profileRepository = services.GetRequiredService<IProfileRepository>();
            var profile = await profileRepository.GetAsync(cancellationToken);
            var savedPlacements = await services
                .GetRequiredService<IPetPlacementRepository>()
                .ListAsync(cancellationToken);
            // The host starts from a safe neutral placement so its first
            // monitor snapshot describes the monitor it actually occupies.
            // Saved placements are selected only after that identity is known.
            var initialPlacement = new PetPlacement("MISSING", 0.8, 0.8, 1);
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
            composer.SetActionSurface(actionSurface);
            composer.SetOverlayPalette(OverlaySurfacePalette.For(
                preferences.Theme,
                OverlaySurfaceRenderer.IsHighContrastEnabled()));
            presenter = new LayeredFramePresenter();
            startup = new StartupRegistrationService();
            WindowsCompanionRuntime? activeRuntime = null;
            var activePlacement = initialPlacement;
            var preferenceMutations = new PreferenceMutationCoordinator(
                preferences,
                preferencesRepository,
                (updated, token) => (activeRuntime
                    ?? throw new InvalidOperationException("The companion runtime is not ready."))
                    .ApplySettingsAsync(updated, activePlacement, token));
            var startupSettings = new StartupSettingsService(
                startup,
                preferenceMutations);
            try
            {
                if (profile?.OnboardingComplete == true)
                {
                    await startupSettings.RetryStartupRegistrationAsync(cancellationToken);
                }
                else
                {
                    await startupSettings.ReconcileExternalAsync(false, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Dudu startup registration reconciliation failed: {0}", exception);
            }
            var pause = new PauseStateStore();
            var runtimePreferences = new RuntimePreferencesState(preferences);
            var showOverlay = launchOptions.ShouldShowOverlay(preferences, profile);

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
                {
                    var router = new CompanionCommandRouter(
                        lifecycle,
                        pause,
                        token => actions.OpenSettings(startupSettings, token),
                        actions.Exit);
                    return command => _ = ObserveNativeCallbackAsync(
                        router.HandleAsync(command),
                        $"tray-{command}");
                },
                initializeOverlay: async overlay =>
                {
                    animationEngine = new AnimationEngine(
                        pack,
                        overlay,
                        composer: composer);
                    presentationCoordinator = new PetPresentationCoordinator(
                        pet,
                        animationEngine.PlayAsync,
                        () => new AnimationOptions
                        {
                            ReducedMotionEnabled = runtimePreferences.Current.ReducedMotion,
                        });
                    _ = StartAnimationPlayback(
                        animationEngine.PlayAsync(
                            pet.Current,
                            preferences.ReducedMotion
                                ? AnimationOptions.ReducedMotion
                                : AnimationOptions.Default,
                            cancellationToken));
                    await overlay.SetActionSurfaceAsync(actionSurface, cancellationToken);
                },
                initialUserVisible: showOverlay,
                isQuietHours: () => QuietHoursPolicy.IsQuiet(
                    DateTimeOffset.UtcNow,
                    runtimePreferences.Current.QuietHours,
                    TimeZoneInfo.Local),
                onPreferencesChanged: (updated, token) =>
                {
                    runtimePreferences.Set(updated);
                    composer.SetOverlayPalette(OverlaySurfacePalette.For(
                        updated.Theme,
                        OverlaySurfaceRenderer.IsHighContrastEnabled()));
                    if (animationEngine is null)
                    {
                        return Task.CompletedTask;
                    }

                    return StartAnimationPlayback(
                        animationEngine.PlayAsync(
                            pet.Current,
                            updated.ReducedMotion
                                ? AnimationOptions.ReducedMotion
                                : AnimationOptions.Default,
                            token));
                },
                cancellationToken: cancellationToken);
            activeRuntime = runtime;

            var placementSnapshot = await runtime.CapturePlacementSnapshotAsync(cancellationToken);
            var currentMonitorPlacement = savedPlacements.FirstOrDefault(item =>
                string.Equals(
                    item.MonitorDeviceName,
                    placementSnapshot.Monitor.DeviceName,
                    StringComparison.Ordinal))
                ?? placementSnapshot.Placement;
            activePlacement = currentMonitorPlacement;
            await runtime.ApplySettingsAsync(preferences, currentMonitorPlacement, cancellationToken);
            var featureContext = new CompanionFeatureContext(
                services.GetRequiredService<IClock>(),
                preferenceMutations,
                profileRepository,
                services.GetRequiredService<IPetPlacementRepository>(),
                services.GetRequiredService<IReminderRepository>(),
                services.GetRequiredService<IReminderRepository>() as IReminderWriter
                    ?? throw new InvalidOperationException("Reminder writer is not registered."),
                services.GetRequiredService<ITaskRepository>(),
                services.GetRequiredService<IFocusSessionRepository>(),
                services.GetRequiredService<ILocalNoteRepository>(),
                services.GetRequiredService<IRemoteEnvelopeRepository>(),
                services.GetRequiredService<ICountdownRepository>(),
                services.GetRequiredService<ICheckInRepository>(),
                services.GetRequiredService<Dudu.Core.CheckIns.CheckInService>(),
                services.GetRequiredService<Dudu.Core.Tasks.TaskService>(),
                services.GetRequiredService<Dudu.Core.Focus.FocusService>(),
                services.GetRequiredService<Dudu.Core.Notes.LocalNoteSelector>(),
                services.GetRequiredService<IPairingService>(),
                services.GetRequiredService<ICompanionFeatureTransactions>(),
                pet,
                applyPlacementAsync: runtime.ApplyPlacementAsync,
                setUserVisibleAsync: runtime.SetUserVisibleAsync,
                getPauseState: () => pause.GetEffective(DateTimeOffset.UtcNow),
                applyPauseAsync: async (state, token) =>
                {
                    pause.Set(state);
                    await runtime.SetUserVisibleAsync(state.Mode == PauseMode.None, token);
                },
                presentPetAsync: (petEvent, token) =>
                {
                    pet.Handle(petEvent);
                    if (animationEngine is not null)
                    {
                        _ = StartAnimationPlayback(animationEngine.PlayAsync(
                            pet.Current,
                            new AnimationOptions
                            {
                                ReducedMotionEnabled = runtimePreferences.Current.ReducedMotion,
                            },
                            token));
                    }
                    return Task.CompletedTask;
                },
                presentOneShotPetAsync: (petEvent, dismissalId, token) =>
                    (presentationCoordinator ?? throw new InvalidOperationException(
                        "The pet presentation coordinator is not ready."))
                    .PresentOneShotAsync(petEvent, dismissalId, token),
                backupAsync: async token =>
                {
                    var path = await services.GetRequiredService<DatabaseBackupService>()
                        .CreatePreMigrationBackupAsync(token);
                    if (path is null)
                    {
                        throw new InvalidOperationException("There is no local database to back up.");
                    }
                },
                restoreAsync: async token =>
                {
                    var result = await services.GetRequiredService<DatabaseBackupService>()
                        .RestoreLatestValidAsync(token);
                    if (!result.Restored)
                    {
                        throw new InvalidOperationException(result.Failure switch
                        {
                            RestoreFailure.NotFound => "No local backup is available to restore.",
                            RestoreFailure.IntegrityCheckFailed => "No valid local backup could be restored.",
                            _ => "The latest local backup could not be restored.",
                        }, result.Exception);
                    }
                },
                deleteLocalDataAsync: token => services
                    .GetRequiredService<LocalDataMaintenanceService>()
                    .DeleteAllUserDataAsync(token),
                applyOutfitAsync: (outfit, token) =>
                {
                    if (animationEngine is null)
                    {
                        throw new NotSupportedException("Outfits are not available in this companion runtime.");
                    }
                    _ = StartAnimationPlayback(animationEngine.PlayAsync(
                        pet.Current,
                        new AnimationOptions
                        {
                            ReducedMotionEnabled = runtimePreferences.Current.ReducedMotion,
                            OutfitKey = outfit,
                        }, token));
                    return Task.CompletedTask;
                },
                setGlobalShortcutAsync: runtime.SetGlobalShortcutAsync,
                deleteRemoteDataAsync: async token =>
                {
                    var result = await services.GetRequiredService<IPairingService>()
                        .DeleteRemoteDeviceWithResultAsync(cancellationToken: token);
                    if (!result.Completed)
                    {
                        throw new NotSupportedException(result.ErrorMessage ?? "Remote-device deletion is unavailable.");
                    }
                });
            var overlayRouter = new OverlayCommandRouter(
                featureContext,
                (destination, token) => DispatchSettingsDestinationAsync(actions, destination, token));
            actionSurface.Bind(overlayRouter);
            actions.ConfigureSettings?.Invoke(new CompanionSettingsContext(
                startupSettings,
                startup,
                preferenceMutations,
                profileRepository,
                services.GetRequiredService<IPetPlacementRepository>(),
                services.GetRequiredService<IAppUnitOfWork>(),
                services.GetRequiredService<IPairingService>(),
                profile,
                currentMonitorPlacement,
                placementSnapshot with { Placement = currentMonitorPlacement },
                runtime.ApplySettingsAsync,
                runtime.CapturePlacementSnapshotAsync,
                runtime.ApplyPlacementAsync,
                runtime.SetUserVisibleAsync)
            {
                Features = featureContext,
                ActionSurface = actionSurface,
                OverlayCommands = overlayRouter,
            });
            return new ComposedPrimaryRuntime(runtime, animationEngine!, presenter, startup, services);
        }
        catch
        {
            startup?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (animationEngine is not null)
            {
                await animationEngine.DisposeAsync();
            }
            else
            {
                composer?.Dispose();
            }
            presenter?.Dispose();
            await host.DisposeAsync();
            await services.DisposeAsync();
            throw;
        }
    }

    private static Task StartAnimationPlayback(Task playback)
    {
        _ = ObserveAnimationAsync(playback);
        return Task.CompletedTask;
    }

    internal static Task DispatchSettingsDestinationAsync(
        CompanionUiActions actions,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();
        return actions.NavigateSettingsDestination?.Invoke(destination, cancellationToken)
            ?? Task.FromException(new InvalidOperationException(
                "Dudu could not dispatch settings navigation to the UI thread."));
    }

    private static async Task ObserveAnimationAsync(Task playback)
    {
        try
        {
            await playback;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu animation playback failed: {0}", exception);
        }
    }

    internal static async Task ObserveNativeCallbackAsync(Task callback, string operation)
    {
        try
        {
            await callback;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu native callback '{0}' failed: {1}", operation, exception);
        }
    }

    private sealed class ComposedPrimaryRuntime(
        WindowsCompanionRuntime runtime,
        AnimationEngine animationEngine,
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
            await animationEngine.DisposeAsync();
            await runtime.DisposeAsync();
            await startup.DisposeAsync();
            presenter.Dispose();
            await services.DisposeAsync();
        }
    }
}
