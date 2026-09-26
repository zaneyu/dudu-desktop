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

    /// <summary>
    /// True when this context was configured by <c>CreateSafeModeRuntimeAsync</c>:
    /// no tray and no overlay are composed, so the settings window is the only
    /// UI surface. App.xaml.cs uses this to exit when that window closes.
    /// </summary>
    public bool IsSafeMode { get; init; }
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
    /// Every launch asks for the companion to be shown, including the
    /// background launch at sign-in: a desktop pet that is invisible after
    /// boot reads as broken. Pause, lock and fullscreen still veto the show
    /// downstream in AppLifecycleCoordinator.TryCanShow -- quiet hours gates
    /// proactive presentation only, never the overlay's own visibility.
    /// </summary>
    public bool ShouldShowOverlay(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return true;
    }

    public bool ShouldOpenSettings(Profile? profile) =>
        profile?.OnboardingComplete != true || !Background;

    public bool ShouldShowOverlay(Preferences preferences, Profile? profile) =>
        profile?.OnboardingComplete == true && ShouldShowOverlay(preferences);
}

/// <summary>
/// Overrides Dudu.Infrastructure's Null* safe defaults for
/// <see cref="IReminderDueSink"/> and <see cref="IRemoteNoteArrivalSink"/>
/// with the real WinUI-backed sinks. Extracted out of the composition root so
/// a real <see cref="IServiceCollection"/> can be built and its resolved
/// types asserted in a test, instead of only pattern-matching the
/// composition source as text -- a source-text test still passes with the
/// registration commented out or reordered.
/// </summary>
internal static class ProductionPresentationSinks
{
    internal static IServiceCollection AddProductionPresentationSinks(
        this IServiceCollection services,
        Func<IUnsolicitedPresentationGateway> gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        return services
            .AddSingleton<IReminderDueSink>(provider => new ReminderDueSink(
                provider.GetRequiredService<IReminderRepository>(),
                gateway,
                profiles: provider.GetRequiredService<IProfileRepository>()))
            .AddSingleton<IRemoteNoteArrivalSink>(provider => new RemoteNoteArrivalSink(gateway));
    }
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
        IUnsolicitedPresentationGateway ResolveGateway() =>
            presentationGateway
                ?? throw new InvalidOperationException("The presentation gateway is not ready.");
        var services = new ServiceCollection()
            .AddDuduInfrastructure(
                new DatabaseOptions(paths.Database, paths.Backups),
                // Release builds leave DUDU_RELAY_BASE_URL unset: the environment override is an
                // explicit local-dev opt-in (see RelayConfiguration), never part of release
                // handoff. The resolved origin is traced at startup without secrets.
                RelayConfiguration.Resolve(logResolvedBaseUrl: static origin =>
                    Trace.TraceInformation("Dudu relay base URL resolved to {0}.", origin)))
            .AddFileDiagnosticLogging(paths)
            .AddProductionPresentationSinks(ResolveGateway)
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
            // A transient SQLITE_BUSY/LOCKED here (an AV scanner or OneDrive briefly holding
            // dudu.db) would otherwise drop straight into safe mode for the whole session:
            // Database's own bounded transient retry (DatabaseAccessCoordinator) never gets a
            // chance to run, because that retry only fires on a SUBSEQUENT InitializeAsync call
            // and db-init is the very first one. Retry it here instead, once, separated by the
            // same cooldown the coordinator itself waits between retries -- capped at 1 (not the
            // coordinator's own cap of 3): this runs before any UI is shown and while still
            // holding the single-instance mutex, so more than one cooldown wait would block
            // startup for tens of seconds with nothing on screen. A non-transient failure (a
            // corrupt file, CANTOPEN, FULL) retries zero times, exactly as before. The wait must
            // be at least Database.TransientBusyRetryCooldown -- any shorter and the coordinator's
            // own CanRetryTransientFailureNow() cooldown check has not elapsed yet, so it would
            // hand back the same still-latched faulted task instead of actually re-running
            // InitializeCoreAsync.
            const int maxTransientDbInitRetries = 1;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await RunStartupPhaseAsync(
                        "db-init",
                        () => database.InitializeAsync(cancellationToken));
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (StartupPhaseException exception)
                {
                    if (attempt < maxTransientDbInitRetries && Database.IsTransientBusyOrLocked(exception))
                    {
                        await Task.Delay(Database.TransientBusyRetryCooldown, cancellationToken);
                        continue;
                    }

                    // A database that cannot open (SQLITE_BUSY/CANTOPEN/FULL, etc.) must not
                    // silently exit the process: fall into safe mode below so she still sees
                    // the settings surface and the safe-mode notice instead of nothing at all.
                    // The failure itself is already recorded by the Database.FailureReporter
                    // hook wired above.
                    databaseUnavailable = true;
                    safeMode = true;
                    break;
                }
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
                    pet,
                    databaseUnavailable);
            }

            // L2 / merge note: subscribed only past the safe-mode return
            // above, so a safe-mode run (overlay and remote sync disabled)
            // never wires it — there is no presentationCoordinator for it to
            // ever resolve to in that mode, and the raw pet.Handle fallback
            // below would be the only thing running for the rest of the
            // process's life. FocusService itself lives for the process
            // lifetime, so this subscription is never explicitly removed;
            // it is fine to leak for that duration, same as every other
            // handler this composition method wires up.
            //
            // The 30-second reminder tick (ReminderEngine.TickAsync, via
            // AppHost) completes an expired focus session on the user's
            // behalf, but nothing else ever raises FocusEnded for that path
            // (only TasksFocusViewModel.EndFocusAsync does, for a manual
            // end) — without this, the pet stays latched in Focus, which
            // suppresses every reminder and note presentation until restart.
            // Route it through the same one-shot path a manual end uses.
            services.GetRequiredService<Dudu.Core.Focus.FocusService>().SessionExpired += focusId =>
            {
                var focusEnded = new PetEvent.FocusEnded(focusId.ToString("D"));
                // M3: apply synchronously and first, so a reminder or note
                // becoming due on the very same 30-second tick sees the
                // post-FocusEnded state deterministically. Previously this
                // ordering only held via PetPresentationCoordinator's
                // internal petGate SemaphoreSlim happening to still be
                // uncontended — fragile, not a real guarantee. FocusEnded is
                // idempotent (confirmed in PetStateMachine.Handle: a second
                // call with the same FocusId is a no-op once _focusId is
                // already null), so PresentOneShotAsync's own internal
                // pet.Handle(focusEnded) below is a safe replay, not a
                // double transition.
                pet.Handle(focusEnded);

                Task callback;
                if (presentationCoordinator is null)
                {
                    // Composed inside initializeOverlay below; a session
                    // expiring before that (implausible this early in
                    // startup, but not worth crashing over) still needs to
                    // self-clear the FocusTransition latch the same way the
                    // real one-shot path would (M2) — nothing else will
                    // ever raise Dismissed("focus-end") for it otherwise.
                    pet.Handle(PetEvent.CompletionForOneShot(focusEnded, "focus-end"));
                    callback = Task.CompletedTask;
                }
                else
                {
                    callback = presentationCoordinator.PresentOneShotAsync(focusEnded, "focus-end", CancellationToken.None);
                }

                _ = ObserveNativeCallbackAsync(callback, "focus-expiry", host.ErrorReporter);
            };

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
            // Seeded with the live fullscreen reading so "pause until
            // fullscreen ends" chosen from inside a fullscreen session still
            // ends with that session (see PauseStateStore.OnFullscreenChanged).
            var pause = new PauseStateStore(() => presentationGateway?.IsFullscreen ?? false);
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
                // The same pause gate every other surface uses: "pause until
                // fullscreen ends" only mutes while fullscreen is actually
                // active, instead of muting audio for as long as the mode is
                // merely set.
                isPaused: () =>
                {
                    var now = DateTimeOffset.UtcNow;
                    return PausePolicy.IsSuppressed(
                        pause.GetEffective(now),
                        now,
                        presentationGateway?.IsFullscreen ?? false);
                },
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
                trayLabelOverride: command =>
                    CompanionCommandRouter.TrayLabelFor(command, pause, DateTimeOffset.UtcNow),
                fullscreenObserved: fullscreenNow => pause.OnFullscreenChanged(fullscreenNow),
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
                    var toastReminders = services.GetRequiredService<IReminderRepository>();
                    var reminderToastActions = new ReminderToastActions(
                        services.GetRequiredService<IClock>(),
                        toastReminders,
                        toastReminders as IReminderWriter
                            ?? throw new InvalidOperationException("Reminder writer is not registered."),
                        invokedNotifications.DismissReminderAsync,
                        // presentationGateway is composed just below, before
                        // any toast can exist to be clicked.
                        (reminderId, token) =>
                            presentationGateway?.DiscardHeldAsync(PresentationItemKind.Reminder, reminderId, token)
                                ?? Task.CompletedTask,
                        host.ErrorReporter);
                    try
                    {
                        AppNotificationManager.Default.NotificationInvoked += (_, invokedArgs) =>
                            HandleNotificationInvoked(
                                actions,
                                invokedNotifications,
                                invokedArgs.Arguments,
                                reminderToastActions,
                                host.ErrorReporter);
                    }
                    catch (Exception exception)
                    {
                        // Toast activation is optional: a broken Windows App
                        // Runtime registration must never fail startup. Durable
                        // events still reach the user through the pet bubble.
                        Trace.TraceWarning("Dudu toast activation unavailable: {0}", exception.Message);
                    }
                    if (database.LastRecoveryOutcome != DatabaseRecoveryOutcome.None)
                    {
                        // Best-effort, one-shot: db-init already recorded the recovery via
                        // Database.FailureReporter above. This is the user-facing half -- a
                        // silently repaired or silently emptied database is exactly the
                        // "she never finds out" outcome this notice exists to prevent. Must
                        // never block or fail startup, so it is fired and observed the same
                        // way every other native callback here is.
                        //
                        // Register before showing, same as SafeModePrimaryRuntime's own
                        // ShowSafeModeNoticeAsync below: AppNotificationManager only registers
                        // later, from PresentationCoordinator.StartAsync (via AppHost.StartAsync,
                        // after this method returns) -- Show() before that first registration
                        // throws, and ShowIfAvailableAsync's catch would swallow it and flip
                        // NotificationsAvailable false, losing this exact notice. TryRegisterAsync
                        // is idempotent (AppNotificationService's _registrationGate/_registered
                        // latch a successful registration), so PresentationCoordinator.StartAsync's
                        // later call is a safe no-op.
                        _ = ObserveNativeCallbackAsync(
                            NotifyDataRecoveryAsync(invokedNotifications, database.LastRecoveryOutcome, cancellationToken),
                            "data-recovery-notice",
                            host.ErrorReporter);

                        static async Task NotifyDataRecoveryAsync(
                            AppNotificationService notifications,
                            DatabaseRecoveryOutcome outcome,
                            CancellationToken token)
                        {
                            await notifications.TryRegisterAsync(token);
                            await notifications.ShowDataRecoveryNoticeAsync(outcome, token);
                        }
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
                        playAudioAsync: (cue, token) => audioCueService.TryPlayAsync(cue, token),
                        // The scheduler has no access to the loaded pack, so the
                        // sticker keys it may roll are passed in here instead —
                        // otherwise a number the pack has no art for (e.g. the
                        // shipped pack currently has no sticker-018/027) silently
                        // resolves to the base idle loop via AssetPack.ResolveAnimation.
                        // Curated ambient rotation: exclude scold/spank/submissive/
                        // angry/moody poses with legible text (001,005,008,014,
                        // 021) until text is cropped + tone approved.
                        availableStickerKeys: pack.Manifest.Outfits.TryGetValue("base", out var baseOutfit)
                            ? baseOutfit.Animations.Keys
                                .Where(AssetManifestContract.IsStickerAnimationKey)
                                .Where(key => key is not (
                                    "sticker-001" or "sticker-005" or "sticker-008"
                                    or "sticker-014" or "sticker-021"))
                                .ToArray()
                            : null,
                        // P2-B: so an item held by quiet hours/fullscreen/lock/pause
                        // survives a quit or crash instead of only living in
                        // PresentationPolicy's in-memory queue.
                        heldPresentations: services.GetRequiredService<IHeldPresentationRepository>(),
                        // Finding B: the events sink and startup visibility
                        // gate that push real fullscreen/lock/visibility
                        // state both run later, after this method returns --
                        // this gateway must start user-hidden so the
                        // startup reminder tick's direct call into the
                        // reminder engine (AppHost.RunReminderTickAsync
                        // still runs that unconditionally; only its
                        // reconcile and its queue release are deferred via
                        // releasePresentations: false, see the comment
                        // there) cannot animate an overdue reminder straight
                        // into a window that is not shown yet and delete
                        // its row on that "successful" presentation.
                        initialUserHidden: true);
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
                    },
                    visible => presentationGateway?.SetUserVisible(visible)),
                cancellationToken: cancellationToken,
                errorReporter: host.ErrorReporter,
                presentOneShotAsync: (petEvent, dismissalId, token) =>
                {
                    // presentationCoordinator is composed inside initializeOverlay
                    // above; a lifecycle event racing ahead of that (implausible,
                    // but not worth crashing over) falls back to a raw pet update
                    // instead of throwing. M2: also self-complete it (as the real
                    // one-shot path would) so the fallback never leaves the event
                    // latched with nothing left to ever dismiss it.
                    if (presentationCoordinator is null)
                    {
                        pet.Handle(petEvent);
                        pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
                        return Task.CompletedTask;
                    }

                    return presentationCoordinator.PresentOneShotAsync(petEvent, dismissalId, token);
                }));
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
                    // Re-evaluate the pause gate only -- do not write the
                    // desired-visible flag. Writing it here would make a
                    // timed pause permanently hide her once it expires,
                    // since nothing restores _userVisible afterward.
                    await runtime.OnPauseStateChangedAsync(token);
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
                discardHeldReminderAsync: (reminderId, token) =>
                    presentationGateway?.DiscardHeldAsync(PresentationItemKind.Reminder, reminderId, token)
                        ?? Task.CompletedTask,
                discardHeldLocalNoteAsync: (noteId, token) =>
                    presentationGateway?.DiscardHeldAsync(PresentationItemKind.LocalNote, noteId, token)
                        ?? Task.CompletedTask,
                discardHeldRemoteNoteAsync: (messageId, token) =>
                    presentationGateway?.DiscardHeldAsync(PresentationItemKind.RemoteNote, messageId, token)
                        ?? Task.CompletedTask,
                discardHeldRemoteNotesAsync: token =>
                    presentationGateway?.DiscardHeldByKindAsync(PresentationItemKind.RemoteNote, token)
                        ?? Task.CompletedTask,
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
        // Ambient poses (idle/focus) loop forever; don't gate audio on visual
        // completion or the cue never plays and the task leaks.
        var timeout = Task.Delay(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(visualPlayback, timeout);
        if (completed != visualPlayback)
        {
            await ObserveAudioCueAsync(playAudioAsync);
            return;
        }

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
    /// backup-prune/maintenance ("backup-prune"), and remote sync
    /// ("remote-sync-*") failures leave a persisted phase-named diagnostic.
    /// Hook-only: every failure still propagates to its caller exactly as
    /// before, so a failing secret write during registration still leaves
    /// the device-id-last completion-marker ordering intact.
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
        // RemoteSyncService is only registered when a relay base URL is configured (see
        // DependencyInjection.AddDuduInfrastructure), so a release build without one must not
        // fail resolution here.
        if (services.GetService<RemoteSyncService>() is { } remoteSync)
        {
            remoteSync.FailureReporter =
                (phase, exception) => StartupFailureLogger.Record(paths, phase, exception);
        }
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
        PetStateMachine pet,
        bool databaseUnavailable)
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
            IsSafeMode = true,
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
            notifications,
            databaseUnavailable);
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
    /// Handles a toast activation. A reminder toast's Done/Snooze buttons
    /// carry out the action directly through <see cref="ReminderToastActions"/>
    /// (they used to only open the Reminders page and change nothing); its
    /// body opens the Reminders page, as does a Done/Snooze that could not be
    /// applied (reminder gone or changed, or a failed write). A malformed or
    /// unknown activation is ignored rather than throwing.
    /// </summary>
    private static void HandleNotificationInvoked(
        CompanionUiActions actions,
        AppNotificationService notifications,
        IEnumerable<KeyValuePair<string, string>>? arguments,
        ReminderToastActions? reminderActions,
        IAppHostErrorReporter? errorReporter)
    {
        // Review I2: this used to re-parse invokedArgs.Argument, the raw string, with a
        // parser that split on '&' while the Windows App SDK writes ';' -- so every click
        // resolved to null. invokedArgs.Arguments is the SDK's own parsed map, which needs
        // no separator convention at all.
        var activation = NotificationActivation.TryParse(arguments);
        if (activation is null)
        {
            return;
        }

        _ = ObserveNativeCallbackAsync(
            HandleNotificationActivationAsync(
                actions,
                notifications,
                activation,
                reminderActions,
                errorReporter,
                CancellationToken.None),
            "notification-invoked",
            errorReporter);
    }

    internal static async Task HandleNotificationActivationAsync(
        CompanionUiActions actions,
        AppNotificationService notifications,
        NotificationActivation activation,
        ReminderToastActions? reminderActions,
        IAppHostErrorReporter? errorReporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(activation);
        switch (activation.Action)
        {
            case NotificationActivationAction.OpenNote:
                await DispatchSettingsDestinationAsync(actions, "notes", cancellationToken);
                return;
            case NotificationActivationAction.OpenReminder:
                await DispatchSettingsDestinationAsync(actions, "reminders", cancellationToken);
                return;
            case NotificationActivationAction.ReminderDone:
            case NotificationActivationAction.ReminderSnooze:
                break;
            default:
                return;
        }

        if (activation.ReminderId is not { } reminderId)
        {
            return;
        }

        var applied = false;
        if (reminderActions is not null)
        {
            try
            {
                applied = activation.Action == NotificationActivationAction.ReminderDone
                    ? await reminderActions.CompleteAsync(reminderId, cancellationToken)
                    : await reminderActions.SnoozeAsync(reminderId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                WindowsCompanionRuntime.ReportStaticFailure(errorReporter, "notification-reminder-action", exception);
            }
        }

        if (applied)
        {
            return;
        }

        // Could not apply it from the toast: acting on the toast still
        // acknowledges it, so drop the Action Center copy, and open the
        // Reminders page so she can finish it there.
        try
        {
            await notifications.DismissReminderAsync(reminderId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WindowsCompanionRuntime.ReportStaticFailure(errorReporter, "notification-dismiss", exception);
        }

        await DispatchSettingsDestinationAsync(actions, "reminders", cancellationToken);
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
        AppNotificationService? notifications,
        bool databaseUnavailable) : IPrimaryAppRuntime
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
            await notifications.ShowSafeModeNoticeAsync(databaseUnavailable, token);
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

            notifications?.Dispose();
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
        Action<bool> setAnimationSuppressed,
        Action<bool> setUserVisible) : IPresentationEnvironmentSink
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

        // H1(b): forwarded straight through to presentationGateway, unlike
        // locked/fullscreen this does not also pause the animation engine —
        // the overlay window's own Show/Hide already governs whether it
        // renders anything while the user has hidden Dudu from the tray.
        public void SetUserVisible(bool visible) => setUserVisible(visible);
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
