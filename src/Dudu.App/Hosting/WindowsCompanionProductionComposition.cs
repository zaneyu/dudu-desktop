using System.Diagnostics;
using Dudu.App.Animation;
using Dudu.App.Audio;
using Dudu.App.Notifications;
using Dudu.App.Overlay;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Policies;
using Dudu.Core.Time;
using Dudu.Core;
using Dudu.Infrastructure;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppNotifications;

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
    public IReadOnlyList<string> AvailableOutfitKeys { get; init; } = ["base"];
}

public sealed record CompanionLaunchOptions(bool Background, bool SelfTest = false)
{
    public static CompanionLaunchOptions Parse(string? arguments)
    {
        var tokens = (arguments ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var background = tokens.Any(argument => string.Equals(
            argument,
            "--background",
            StringComparison.OrdinalIgnoreCase));
        var selfTest = tokens.Any(argument => string.Equals(
            argument,
            "--self-test",
            StringComparison.OrdinalIgnoreCase));
        return new CompanionLaunchOptions(background, selfTest);
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
    /// <summary>
    /// Minimum silence between unsolicited releases once a suppressed queue
    /// starts draining, so leaving quiet hours/fullscreen/lock/pause drains
    /// one durable item at a time instead of a burst.
    /// </summary>
    private static readonly TimeSpan PresentationMinimumSilentInterval = TimeSpan.FromSeconds(90);

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

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            // The app targets Windows 11 24H2 (build 26100) for its WinRT and
            // Win32 surface. Failing here records a clear os-version phase in
            // startup-failure.log instead of a cryptic native error later.
            throw new StartupPhaseException(
                "os-version",
                new PlatformNotSupportedException(
                    "Dudu Desktop requires Windows 11 version 24H2 (build 26100) or newer."));
        }

        var paths = AppPaths.ForCurrentUser();
        // Runs only in the primary instance, so secondary launches never count
        // as failed runs. The counter is incremented before anything is
        // composed and reset once the runtime proves stable or shuts down.
        var crashGuard = StartupCrashGuard.BeginRun(paths.Root);
        var safeMode = crashGuard.SafeMode;
        if (safeMode)
        {
            Trace.TraceWarning(
                "Dudu is starting in safe mode after {0} consecutive failed runs; overlay and remote sync are disabled.",
                crashGuard.ConsecutiveFailedRuns);
        }
        // The real presentation gateway does not exist yet at this point: it
        // depends on the pet state machine, animation engine, and pause
        // store, all of which are only available once the overlay is
        // composed further below. IReminderDueSink must be registered before
        // BuildServiceProvider (ReminderEngine resolves it eagerly), so it
        // captures this variable and resolves the gateway lazily once the
        // rest of the runtime is ready.
        PresentationCoordinator? presentationGateway = null;
        var services = new ServiceCollection()
            .AddDuduInfrastructure(
                new DatabaseOptions(paths.Database, paths.Backups),
                // Release builds leave DUDU_RELAY_BASE_URL unset: the environment override is an
                // explicit local-dev opt-in (see RelayConfiguration), never part of release
                // handoff. The resolved origin is traced at startup without secrets.
                RelayConfiguration.Resolve(logResolvedBaseUrl: static origin =>
                    Trace.TraceInformation("Dudu relay base URL resolved to {0}.", origin)))
            .AddFileDiagnosticLogging(paths)
            .AddSingleton<IReminderDueSink>(provider => new ReminderDueSink(
                provider.GetRequiredService<IReminderRepository>(),
                () => presentationGateway
                    ?? throw new InvalidOperationException("The presentation gateway is not ready.")))
            .AddSingleton<IRemoteNoteArrivalSink>(provider => new RemoteNoteArrivalSink(
                () => presentationGateway
                    ?? throw new InvalidOperationException("The presentation gateway is not ready.")))
            .BuildServiceProvider();
        var host = new AppHost(services, paths);
        WireDataFailureDiagnostics(services, paths);
        if (services.GetService<IReminderDueSink>() is ReminderDueSink reminderDueSink)
        {
            // The sink was eagerly resolved during AppHost construction,
            // before the host's error reporter existed; attach it now so
            // reminder-notify diagnostics reach the shared sink.
            reminderDueSink.ErrorReporter = host.ErrorReporter;
        }

        if (services.GetService<IRemoteNoteArrivalSink>() is RemoteNoteArrivalSink remoteNoteArrivalSink)
        {
            // Same reasoning as the reminder sink above: the host's error
            // reporter does not exist yet when this sink is registered.
            remoteNoteArrivalSink.ErrorReporter = host.ErrorReporter;
        }
        var composer = default(SkiaFrameComposer);
        var presenter = default(LayeredFramePresenter);
        AnimationEngine? animationEngine = null;
        PetPresentationCoordinator? presentationCoordinator = null;
        AppNotificationService? notificationService = null;
        AudioCueService? audioCueService = null;
        IAudioCuePlayer? audioPlayer = null;
        StartupRegistrationService? startup = null;
        var actionSurface = new OverlayActionSurfaceController();

        try
        {
            var database = services.GetRequiredService<Database>();
            var preferencesRepository = services.GetRequiredService<IPreferencesRepository>();
            var profileRepository = services.GetRequiredService<IProfileRepository>();
            var databaseUnavailable = false;
            try
            {
                await RunStartupPhaseAsync(
                    "db-init",
                    () => database.InitializeAsync(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (StartupPhaseException)
            {
                // A database that cannot open (SQLITE_BUSY/CANTOPEN/FULL, etc.) must not
                // silently exit the process: fall into safe mode below so she still sees
                // the settings surface and the safe-mode notice instead of nothing at all.
                // The failure itself is already recorded by the Database.FailureReporter
                // hook wired above.
                databaseUnavailable = true;
                safeMode = true;
            }

            var preferences = databaseUnavailable
                ? services.GetRequiredService<Preferences>()
                : await preferencesRepository.GetAsync(cancellationToken)
                    ?? services.GetRequiredService<Preferences>();
            var profile = databaseUnavailable
                ? null
                : await profileRepository.GetAsync(cancellationToken);
            // The host starts from a safe neutral placement so its first
            // monitor snapshot describes the monitor it actually occupies.
            // Saved placements are selected only after that identity is known.
            var initialPlacement = new PetPlacement("MISSING", 0.8, 0.8, 1);
            var pet = services.GetRequiredService<PetStateMachine>();
            startup = new StartupRegistrationService();
            WindowsCompanionRuntime? activeRuntime = null;
            var activePlacement = initialPlacement;
            var preferenceMutations = new PreferenceMutationCoordinator(
                preferences,
                preferencesRepository,
                (updated, token) => activeRuntime is null
                    ? Task.CompletedTask
                    : activeRuntime.ApplySettingsAsync(updated, activePlacement, token));
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

            if (safeMode)
            {
                return await CreateSafeModeRuntimeAsync(
                    host,
                    services,
                    actions,
                    crashGuard,
                    startup,
                    startupSettings,
                    preferenceMutations,
                    profile,
                    initialPlacement,
                    pet);
            }

            var placementRepository = services.GetRequiredService<IPetPlacementRepository>();
            var savedPlacements = await placementRepository.ListAsync(cancellationToken);
            var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
            var packsRoot = Path.Combine(assetsRoot, "Packs");
            var privateManifestPath = Path.Combine(packsRoot, "private-dudu", "manifest.json");
            var fallbackManifestPath = Path.Combine(packsRoot, "fallback", "manifest.json");
            var manifestPath = File.Exists(privateManifestPath)
                ? privateManifestPath
                : fallbackManifestPath;
            var pack = await RunStartupPhaseAsync(
                "assets",
                async () =>
                {
                    try
                    {
                        return await AssetManifestLoader.LoadAsync(manifestPath, cancellationToken);
                    }
                    catch (AssetManifestException) when (string.Equals(
                        manifestPath,
                        privateManifestPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // The private pack is optional for raw publish folders. If it is absent or
                        // damaged, keep the companion usable with the original neutral fallback.
                        return await AssetManifestLoader.LoadAsync(fallbackManifestPath, cancellationToken);
                    }
                });
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
            var pause = new PauseStateStore();
            var runtimePreferences = new RuntimePreferencesState(preferences);
            var audioManifestPath = ResolveAudioManifestPath(assetsRoot);
            AudioCatalog audioCatalog;
            try
            {
                audioCatalog = await AudioManifestLoader.LoadAsync(
                    audioManifestPath,
                    cancellationToken);
                audioPlayer = new WindowsAudioCuePlayer(
                    Path.GetDirectoryName(audioManifestPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                host.ErrorReporter.Report("audio-manifest-load", exception);
                audioCatalog = NoOpAudioCatalog();
                audioPlayer = new NoOpAudioCuePlayer();
            }
            audioCueService = new AudioCueService(
                audioCatalog,
                audioPlayer,
                () => runtimePreferences.Current,
                isQuietHours: () => QuietHoursPolicy.IsQuiet(
                    DateTimeOffset.UtcNow,
                    runtimePreferences.Current.QuietHours,
                    TimeZoneInfo.Local),
                isPaused: () => pause.GetEffective(DateTimeOffset.UtcNow).Mode != PauseMode.None,
                isFullscreen: () => presentationGateway?.IsFullscreen ?? false,
                isSessionLocked: () => presentationGateway?.IsSessionLocked ?? false,
                isSafeMode: () => safeMode,
                errorReporter: host.ErrorReporter);
            var showOverlay = !safeMode && launchOptions.ShouldShowOverlay(preferences, profile);

            var runtime = await RunStartupPhaseAsync(
                "overlay",
                () => WindowsCompanionRuntime.CreateAsync(
                host,
                presenter,
                initialPlacement,
                MonitorPlacementService.ScaleNominalSize(animation.NominalSize),
                pet,
                preferences,
                pauseState: () => pause.GetEffective(DateTimeOffset.UtcNow),
                openHome: actions.OpenHome,
                persistPlacementAsync: (placement, token) =>
                {
                    activePlacement = placement;
                    return placementRepository.SaveAsync(placement, token);
                },
                trayCommandHandlerFactory: lifecycle =>
                {
                    var router = new CompanionCommandRouter(
                        lifecycle,
                        pause,
                        token => actions.OpenSettings(startupSettings, token),
                        actions.Exit);
                    return command => _ = ObserveNativeCallbackAsync(
                        router.HandleAsync(command),
                        $"tray-{command}",
                        host.ErrorReporter);
                },
                initializeOverlay: async overlay =>
                {
                    animationEngine = new AnimationEngine(
                        pack,
                        overlay,
                        composer: composer,
                        localDate: LocalDateNow(),
                        seasonalDates: SeasonalDatesFor(preferences),
                        localDateProvider: LocalDateNow);
                    // Shared with PresentationCoordinator below so an explicit
                    // one-shot (via PetPresentationCoordinator) and an
                    // unsolicited background release never interleave their
                    // mutation of the one PetStateMachine instance.
                    var petGate = new SemaphoreSlim(1, 1);
                    presentationCoordinator = new PetPresentationCoordinator(
                        pet,
                        animationEngine.PlayAsync,
                        () => new AnimationOptions
                        {
                            ReducedMotionEnabled = runtimePreferences.Current.ReducedMotion,
                            OutfitKey = RuntimeOutfitKey(runtimePreferences.Current),
                        },
                        gate: petGate,
                        playAudioAsync: (presentation, token) =>
                            AudioCueSelection.ForPresentation(presentation) is { } cue
                                ? audioCueService!.TryPlayAsync(cue, token)
                                : Task.CompletedTask);
                    notificationService = new AppNotificationService(
                        new WindowsAppNotificationSink(),
                        errorReporter: host.ErrorReporter);
                    AppNotificationService invokedNotifications = notificationService;
                    try
                    {
                        AppNotificationManager.Default.NotificationInvoked += (_, invokedArgs) =>
                            HandleNotificationInvoked(actions, invokedNotifications, invokedArgs.Arguments);
                    }
                    catch (Exception exception)
                    {
                        // Toast activation is optional: a broken Windows App
                        // Runtime registration must never fail startup. Durable
                        // events still reach the user through the pet bubble.
                        Trace.TraceWarning("Dudu toast activation unavailable: {0}", exception.Message);
                    }
                    presentationGateway = new PresentationCoordinator(
                        new PresentationPolicy(PresentationMinimumSilentInterval),
                        notificationService,
                        pet,
                        animationEngine.PlayAsync,
                        () => new AnimationOptions
                        {
                            ReducedMotionEnabled = runtimePreferences.Current.ReducedMotion,
                            OutfitKey = RuntimeOutfitKey(runtimePreferences.Current),
                        },
                        isQuietHours: () => QuietHoursPolicy.IsQuiet(
                            DateTimeOffset.UtcNow,
                            runtimePreferences.Current.QuietHours,
                            TimeZoneInfo.Local),
                        pauseState: () => pause.GetEffective(DateTimeOffset.UtcNow),
                        petGate: petGate,
                        ambientScheduler: services.GetRequiredService<AmbientScheduler>(),
                        localNoteSelector: services.GetRequiredService<Dudu.Core.Notes.LocalNoteSelector>(),
                        errorReporter: host.ErrorReporter,
                        playAudioAsync: (cue, token) => audioCueService.TryPlayAsync(cue, token));
                    _ = StartAnimationPlayback(
                        animationEngine.PlayAsync(
                            pet.Current,
                            AnimationOptionsFor(preferences),
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
                    animationEngine?.UpdateSeasonalContext(
                        LocalDateNow(),
                        SeasonalDatesFor(updated));
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
                            AnimationOptionsFor(updated),
                            token));
                },
                presentationEnvironment: new DelegatingPresentationEnvironmentSink(
                    locked => presentationGateway?.SetSessionLocked(locked),
                    fullscreenNow => presentationGateway?.SetFullscreen(fullscreenNow),
                    suppressed =>
                    {
                        if (suppressed)
                        {
                            animationEngine?.Pause();
                        }
                        else
                        {
                            animationEngine?.Resume();
                        }
                    }),
                cancellationToken: cancellationToken,
                errorReporter: host.ErrorReporter));
            activeRuntime = runtime;
            host.AttachPresentationGateway(presentationGateway
                ?? throw new InvalidOperationException("The presentation gateway was not composed."));
            var remoteSync = services.GetService<RemoteSyncService>();
            if (remoteSync is not null)
            {
                host.AttachRemoteSync(new RemoteSyncHostAdapter(remoteSync));
            }

            var placementSnapshot = await RunStartupPhaseAsync(
                "runtime-config",
                () => runtime.CapturePlacementSnapshotAsync(cancellationToken));
            var currentMonitorPlacement = savedPlacements.FirstOrDefault(item =>
                string.Equals(
                    item.MonitorDeviceName,
                    placementSnapshot.Monitor.DeviceName,
                    StringComparison.Ordinal))
                ?? placementSnapshot.Placement;
            activePlacement = currentMonitorPlacement;
            await RunStartupPhaseAsync(
                "runtime-config",
                () => runtime.ApplySettingsAsync(preferences, currentMonitorPlacement, cancellationToken));
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
                    var presentation = pet.Handle(petEvent);
                    if (animationEngine is not null)
                    {
                        var playback = animationEngine.PlayAsync(
                            pet.Current,
                            AnimationOptionsFor(runtimePreferences.Current),
                            token);
                        _ = StartAnimationPlayback(playback);
                        if (AudioCueSelection.ForPresentation(presentation) is { } cue)
                        {
                            _ = ObserveDirectAudioAfterVisualAsync(
                                playback,
                                () => audioCueService!.TryPlayAsync(cue, token));
                        }
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
                deleteLocalDataAsync: async token =>
                {
                    // RemoteSyncService only exists when a relay is configured (offline
                    // builds register OfflinePairingService instead); wiping local data
                    // must work either way.
                    var remoteSync = services.GetService<RemoteSyncService>();
                    if (remoteSync is not null)
                    {
                        await remoteSync.StopAsync(token);
                    }

                    await services.GetRequiredService<LocalDataMaintenanceService>()
                        .DeleteAllUserDataAsync(token);
                },
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
                            OutfitKey = outfit ?? RuntimeOutfitKey(runtimePreferences.Current),
                        }, token));
                    return Task.CompletedTask;
                },
                setGlobalShortcutAsync: runtime.SetGlobalShortcutAsync,
                dismissReminderNotificationAsync: (reminderId, token) =>
                    notificationService?.DismissReminderAsync(reminderId, token) ?? Task.CompletedTask,
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
                AvailableOutfitKeys = pack.Manifest.Outfits.Keys
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray(),
            });
            await FixtureRemoteNoteInstaller.InstallIfRequestedAsync(services, cancellationToken);
            return new ComposedPrimaryRuntime(
                runtime,
                animationEngine!,
                presenter,
                startup,
                services,
                crashGuard,
                audioCueService,
                audioPlayer);
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
            await DisposeAudioAsync(audioCueService, audioPlayer);
            await host.DisposeAsync();
            await services.DisposeAsync();
            throw;
        }
    }

    private static AudioCatalog NoOpAudioCatalog() => new(
        [
            new AudioSoundPack("bubu-dudu-atata", []),
            new AudioSoundPack("tata-lala", []),
            new AudioSoundPack("dudu-lalala", []),
            new AudioSoundPack("dudu-atatata", []),
            new AudioSoundPack("dudu-yapapa", []),
        ]);

    internal static string ResolveAudioManifestPath(string assetsRoot) =>
        Path.Combine(assetsRoot, "Audio", "private-dudu", "manifest.json");

    internal static async Task ObserveDirectAudioAfterVisualAsync(
        Task visualPlayback,
        Func<Task> playAudioAsync)
    {
        try
        {
            await visualPlayback;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu direct animation failed: {0}", exception.GetType().FullName);
            return;
        }

        await ObserveAudioCueAsync(playAudioAsync);
    }

    private static async Task ObserveAudioCueAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception)
        {
            Trace.TraceError("Dudu direct audio cue failed: {0}", exception.GetType().FullName);
        }
    }

    private static ValueTask DisposeAudioAsync(
        AudioCueService? service,
        IAudioCuePlayer? player)
    {
        if (service is not null)
        {
            return service.DisposeAsync();
        }

        if (player is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();
        (player as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class NoOpAudioCuePlayer : IAudioCuePlayer, IAudioCuePlayerLifecycle
    {
        public Task<AudioPlaybackState> PlayAsync(
            AudioCue cue,
            double volume,
            CancellationToken cancellationToken) =>
            Task.FromResult(AudioPlaybackState.Suppressed);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Task StartAnimationPlayback(Task playback)
    {
        _ = ObserveAnimationAsync(playback);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects the data layer's failure hooks to the redacting
    /// <see cref="StartupFailureLogger"/> so database initialization
    /// ("db-init"), DPAPI secret read/write ("secret-read"/"secret-write"),
    /// and backup-prune/maintenance ("backup-prune") failures leave a
    /// persisted phase-named diagnostic. Hook-only: every failure still
    /// propagates to its caller exactly as before, so a failing secret write
    /// during registration still leaves the device-id-last
    /// completion-marker ordering intact.
    /// </summary>
    private static void WireDataFailureDiagnostics(ServiceProvider services, AppPaths paths)
    {
        services.GetRequiredService<Database>().FailureReporter =
            (phase, exception) => StartupFailureLogger.Record(paths, phase, exception);
        if (services.GetService<ISecretStore>() is DpapiSecretStore secrets)
        {
            secrets.FailureReporter =
                (phase, exception) => StartupFailureLogger.Record(paths, phase, exception);
        }
        services.GetRequiredService<DatabaseBackupService>().FailureReporter =
            (phase, exception) => StartupFailureLogger.Record(paths, phase, exception);
        services.GetRequiredService<LocalDataMaintenanceService>().FailureReporter =
            (phase, exception) => StartupFailureLogger.Record(paths, phase, exception);
    }

    private static async Task RunStartupPhaseAsync(
        string phase,
        Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StartupPhaseException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StartupPhaseException(phase, exception);
        }
    }

    private static async Task<T> RunStartupPhaseAsync<T>(
        string phase,
        Func<Task<T>> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StartupPhaseException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StartupPhaseException(phase, exception);
        }
    }

    private static async Task<IPrimaryAppRuntime> CreateSafeModeRuntimeAsync(
        AppHost host,
        ServiceProvider services,
        CompanionUiActions actions,
        StartupCrashGuard crashGuard,
        StartupRegistrationService startup,
        StartupSettingsService startupSettings,
        PreferenceMutationCoordinator preferenceMutations,
        Profile? profile,
        PetPlacement initialPlacement,
        PetStateMachine pet)
    {
        // Safe mode is deliberately composed before any native overlay object: it only keeps
        // the database, settings services, and the recoverable settings surface alive.
        var profileRepository = services.GetRequiredService<IProfileRepository>();
        var placements = services.GetRequiredService<IPetPlacementRepository>();
        var reminderRepository = services.GetRequiredService<IReminderRepository>();
        var featureContext = new CompanionFeatureContext(
            services.GetRequiredService<IClock>(),
            preferenceMutations,
            profileRepository,
            placements,
            reminderRepository,
            reminderRepository as IReminderWriter
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
            applyPlacementAsync: (_, _) => Task.CompletedTask,
            setUserVisibleAsync: (_, _) => Task.CompletedTask,
            backupAsync: token => CreateBackupAsync(services, token),
            restoreAsync: token => RestoreLatestAsync(services, token),
            deleteLocalDataAsync: async token =>
            {
                // RemoteSyncService only exists when a relay is configured (offline
                // builds register OfflinePairingService instead); wiping local data
                // must work either way.
                var remoteSync = services.GetService<RemoteSyncService>();
                if (remoteSync is not null)
                {
                    await remoteSync.StopAsync(token);
                }

                await services.GetRequiredService<LocalDataMaintenanceService>()
                    .DeleteAllUserDataAsync(token);
            });
        var safeSnapshot = new MonitorPlacementSnapshot(
            initialPlacement,
            new PixelRect(0, 0, 1, 1),
            new MonitorInfo("SAFE-MODE", new PixelRect(0, 0, 1, 1), 96, true));
        actions.ConfigureSettings?.Invoke(new CompanionSettingsContext(
            startupSettings,
            startup,
            preferenceMutations,
            profileRepository,
            placements,
            services.GetRequiredService<IAppUnitOfWork>(),
            services.GetRequiredService<IPairingService>(),
            profile,
            initialPlacement,
            safeSnapshot,
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult(safeSnapshot),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask)
        {
            Features = featureContext,
            AvailableOutfitKeys = ["base"],
        });

        // No overlay is composed in safe mode, so the notification service that
        // normally comes from initializeOverlay does not exist yet; build one
        // here so she still gets the safe-mode notice.
        var notifications = new AppNotificationService(
            new WindowsAppNotificationSink(),
            errorReporter: host.ErrorReporter);
        return new SafeModePrimaryRuntime(
            host,
            services,
            startup,
            actions,
            startupSettings,
            crashGuard,
            notifications);
    }

    private static async Task CreateBackupAsync(
        ServiceProvider services,
        CancellationToken cancellationToken)
    {
        var path = await services.GetRequiredService<DatabaseBackupService>()
            .CreatePreMigrationBackupAsync(cancellationToken);
        if (path is null)
        {
            throw new InvalidOperationException("There is no local database to back up.");
        }
    }

    private static async Task RestoreLatestAsync(
        ServiceProvider services,
        CancellationToken cancellationToken)
    {
        var result = await services.GetRequiredService<DatabaseBackupService>()
            .RestoreLatestValidAsync(cancellationToken);
        if (!result.Restored)
        {
            throw new InvalidOperationException(
                "The latest local backup could not be restored.",
                result.Exception);
        }
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

    /// <summary>
    /// Resolves a toast activation to a settings destination and navigates
    /// there. Executing Done/Snooze directly from the toast is out of scope
    /// for this milestone; a malformed or unknown activation is ignored
    /// rather than throwing.
    /// </summary>
    private static void HandleNotificationInvoked(
        CompanionUiActions actions,
        AppNotificationService notifications,
        IEnumerable<KeyValuePair<string, string>>? arguments)
    {
        // Review I2: this used to re-parse invokedArgs.Argument, the raw string, with a
        // parser that split on '&' while the Windows App SDK writes ';' -- so every click
        // resolved to null. invokedArgs.Arguments is the SDK's own parsed map, which needs
        // no separator convention at all.
        var activation = NotificationActivation.TryParse(arguments);
        var destination = activation?.Action switch
        {
            NotificationActivationAction.OpenNote => "notes",
            NotificationActivationAction.ReminderDone => "reminders",
            NotificationActivationAction.ReminderSnooze => "reminders",
            _ => null,
        };
        if (destination is null)
        {
            return;
        }

        if (activation!.ReminderId is { } reminderId)
        {
            // Acting on the toast acknowledges it; drop the Action Center copy.
            _ = ObserveNativeCallbackAsync(
                notifications.DismissReminderAsync(reminderId, CancellationToken.None),
                "notification-dismiss");
        }

        _ = ObserveNativeCallbackAsync(
            DispatchSettingsDestinationAsync(actions, destination, CancellationToken.None),
            "notification-invoked");
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

    internal static async Task ObserveNativeCallbackAsync(
        Task callback,
        string operation,
        IAppHostErrorReporter? errorReporter = null)
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
            if (errorReporter is not null)
            {
                try { errorReporter.Report(operation, exception); }
                catch { }
                return;
            }

            Trace.TraceError(
                "Dudu native callback '{0}' failed: {1} (0x{2:X8})",
                operation,
                exception.GetType().FullName,
                exception.HResult);
        }
    }

    private sealed class ComposedPrimaryRuntime(
        WindowsCompanionRuntime runtime,
        AnimationEngine animationEngine,
        LayeredFramePresenter presenter,
        StartupRegistrationService startup,
        ServiceProvider services,
        StartupCrashGuard crashGuard,
        AudioCueService? audioCueService,
        IAudioCuePlayer? audioPlayer) : IPrimaryAppRuntime
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _started;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await runtime.StartAsync(cancellationToken);
            Volatile.Write(ref _started, 1);
            _ = crashGuard.MarkCleanAfterAsync(StableRunPeriod, _stopping.Token);
        }

        public Task ActivateAsync(
            AppActivation activation,
            CancellationToken cancellationToken = default) =>
            runtime.ActivateAsync(activation, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            if (Volatile.Read(ref _started) != 0)
            {
                // A started runtime reaching orderly disposal is a clean run.
                crashGuard.MarkCleanRun();
            }

            await animationEngine.DisposeAsync();
            await runtime.DisposeAsync();
            await startup.DisposeAsync();
            presenter.Dispose();
            await DisposeAudioAsync(audioCueService, audioPlayer);
            await services.DisposeAsync();
        }
    }

    private sealed class SafeModePrimaryRuntime(
        AppHost host,
        ServiceProvider services,
        StartupRegistrationService startup,
        CompanionUiActions actions,
        StartupSettingsService startupSettings,
        StartupCrashGuard crashGuard,
        AppNotificationService? notifications) : IPrimaryAppRuntime
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _started;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await actions.OpenSettings(startupSettings, cancellationToken);
            Volatile.Write(ref _started, 1);
            _ = crashGuard.MarkCleanAfterAsync(StableRunPeriod, _stopping.Token);
            if (notifications is not null)
            {
                _ = ObserveNativeCallbackAsync(ShowSafeModeNoticeAsync(), "safe-mode-notice");
            }
        }

        private async Task ShowSafeModeNoticeAsync()
        {
            var token = _stopping.Token;
            await notifications!.TryRegisterAsync(token);
            await notifications.ShowSafeModeNoticeAsync(token);
        }

        public Task ActivateAsync(
            AppActivation activation,
            CancellationToken cancellationToken = default) =>
            actions.OpenSettings(startupSettings, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            if (Volatile.Read(ref _started) != 0)
            {
                crashGuard.MarkCleanRun();
            }

            await host.DisposeAsync();
            await startup.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    /// <summary>
    /// Forwards session-lock/fullscreen transitions to the presentation
    /// gateway, which is not composed yet when <c>WindowsCompanionRuntime</c>
    /// is created. The delegates close over the gateway variable, so calls
    /// made before it is assigned are safely no-ops and every call made
    /// after <c>WindowsCompanionRuntime.StartAsync</c> runs reaches the real
    /// gateway.
    /// Also drives <see cref="AnimationEngine.Pause"/>/<see cref="AnimationEngine.Resume"/>
    /// from the same two signals (Task 15 ruling: reuse this hook rather than
    /// add a second suppression path), suppressing ambient ticks while either
    /// condition holds and resuming only once both have cleared.
    /// </summary>
    /// <summary>How long a started runtime must stay up before its run counts
    /// as clean for the crash-loop safe-mode counter.</summary>
    internal static readonly TimeSpan StableRunPeriod = TimeSpan.FromSeconds(60);

    private static AnimationOptions AnimationOptionsFor(Preferences preferences) =>
        new()
        {
            ReducedMotionEnabled = preferences.ReducedMotion,
            OutfitKey = RuntimeOutfitKey(preferences),
        };

    private static string? RuntimeOutfitKey(Preferences preferences) =>
        preferences.AutomaticSeasonalMode ? null : preferences.OutfitKey ?? "base";

    private static SeasonalDates SeasonalDatesFor(Preferences preferences) =>
        new(preferences.Anniversary, preferences.Birthday);

    private static DateOnly LocalDateNow() =>
        DateOnly.FromDateTime(DateTime.Now);

    private sealed class DelegatingPresentationEnvironmentSink(
        Action<bool> setSessionLocked,
        Action<bool> setFullscreen,
        Action<bool> setAnimationSuppressed) : IPresentationEnvironmentSink
    {
        private bool _locked;
        private bool _fullscreen;

        public void SetSessionLocked(bool locked)
        {
            setSessionLocked(locked);
            _locked = locked;
            setAnimationSuppressed(_locked || _fullscreen);
        }

        public void SetFullscreen(bool fullscreen)
        {
            setFullscreen(fullscreen);
            _fullscreen = fullscreen;
            setAnimationSuppressed(_locked || _fullscreen);
        }
    }

    /// <summary>
    /// Adapts <see cref="RemoteSyncService"/> to the generic <see cref="IAppHostRemoteSync"/>
    /// seam so <c>AppHost</c> stays decoupled from the relay's concrete implementation.
    /// </summary>
    private sealed class RemoteSyncHostAdapter(RemoteSyncService remoteSync) : IAppHostRemoteSync
    {
        public Task StartAsync(CancellationToken cancellationToken = default) =>
            remoteSync.StartAsync(cancellationToken);

        public ValueTask DisposeAsync() => remoteSync.DisposeAsync();
    }
}
