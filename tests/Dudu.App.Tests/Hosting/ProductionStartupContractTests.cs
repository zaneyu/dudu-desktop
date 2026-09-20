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
    public void Production_reminder_due_sink_registration_passes_the_profile_repository()
    {
        // Resolving IReminderDueSink to the ReminderDueSink type (below) would still
        // pass even if the `profiles:` argument were dropped from the registration --
        // it is an optional constructor parameter, so the sink would silently fall
        // back to never personalising a toast with the saved recipient name. This
        // asserts the wiring itself, by source, the same way the null-default
        // override above is asserted.
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        var reminderSinkRegistration = composition.IndexOf(
            "AddSingleton<IReminderDueSink>(provider => new ReminderDueSink(",
            StringComparison.Ordinal);
        var remoteNoteSinkRegistration = composition.IndexOf(
            "AddSingleton<IRemoteNoteArrivalSink>(provider => new RemoteNoteArrivalSink(",
            StringComparison.Ordinal);
        var profilesArgument = composition.IndexOf(
            "profiles: provider.GetRequiredService<IProfileRepository>()",
            StringComparison.Ordinal);

        Assert.True(reminderSinkRegistration >= 0);
        Assert.True(remoteNoteSinkRegistration > reminderSinkRegistration);
        Assert.True(profilesArgument > reminderSinkRegistration);
        Assert.True(profilesArgument < remoteNoteSinkRegistration);
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

    [Fact]
    public void Db_init_retries_a_transient_busy_or_locked_failure_before_falling_into_safe_mode()
    {
        // Pre-handoff audit: a transient SQLITE_BUSY/LOCKED at the very first InitializeAsync
        // call (db-init) used to drop straight into safe mode for the whole session, because
        // Database's own bounded transient retry can only run on a SUBSEQUENT call. This asserts
        // the retry loop classifies the failure with the same public helper the data layer uses,
        // waits the same cooldown, is capped, and still falls into safe mode exactly as before
        // once the cap is spent or the failure is not transient. Capped at 1 (not the
        // coordinator's own cap of 3): this runs before any UI is shown and while still holding
        // the single-instance mutex, so more than one cooldown wait would block startup for tens
        // of seconds with nothing on screen.
        var root = FindRepositoryRoot();
        // Finding 9 (test bug): normalized before the embedded-newline
        // search below, so it does not depend on this checkout's line
        // endings -- a CRLF Windows checkout would otherwise leave a
        // literal "\r\n" where the search string below expects "\n",
        // making IndexOf silently fail to find it.
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs")).Replace("\r\n", "\n");

        var loopStart = composition.IndexOf(
            "const int maxTransientDbInitRetries = 1;",
            StringComparison.Ordinal);
        var databaseInit = composition.IndexOf(
            "\"db-init\",",
            StringComparison.Ordinal);
        var retryCondition = composition.IndexOf(
            "attempt < maxTransientDbInitRetries && Database.IsTransientBusyOrLocked(exception)",
            StringComparison.Ordinal);
        var cooldownDelay = composition.IndexOf(
            "await Task.Delay(Database.TransientBusyRetryCooldown, cancellationToken);",
            StringComparison.Ordinal);
        var safeModeFallback = composition.IndexOf(
            "databaseUnavailable = true;\n                    safeMode = true;",
            StringComparison.Ordinal);

        Assert.True(loopStart >= 0);
        Assert.True(databaseInit > loopStart);
        Assert.True(retryCondition > databaseInit);
        Assert.True(cooldownDelay > retryCondition);
        Assert.True(safeModeFallback > cooldownDelay);
    }

    [Fact]
    public void Successful_startup_shows_a_data_recovery_notice_when_recovery_happened()
    {
        // Pre-handoff audit: Database.LastRecoveryOutcome had zero production consumers, so a
        // corrupt database that was silently restored from backup, or silently started fresh,
        // never told the user. This asserts the notice fires once, only on a successful
        // (non-safe-mode) startup, best-effort through the same ObserveNativeCallbackAsync path
        // every other native callback here uses -- so a failure to notify can never fail startup.
        //
        // Review fix: the first cut called ShowDataRecoveryNoticeAsync directly, before Windows
        // notifications are registered (registration only happens later, from
        // PresentationCoordinator.StartAsync) -- Show() before that first registration throws,
        // and ShowIfAvailableAsync's catch silently swallows it, losing exactly the notice this
        // exists to deliver. This now asserts the register call precedes the show call, matching
        // SafeModePrimaryRuntime.ShowSafeModeNoticeAsync's own register-then-show pattern.
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Dudu.App",
            "Hosting",
            "WindowsCompanionProductionComposition.cs"));

        var safeModeReturn = composition.IndexOf("if (safeMode)", StringComparison.Ordinal);
        var recoveryCheck = composition.IndexOf(
            "database.LastRecoveryOutcome != DatabaseRecoveryOutcome.None",
            StringComparison.Ordinal);
        var registerCall = composition.IndexOf(
            "await notifications.TryRegisterAsync(token);",
            StringComparison.Ordinal);
        var showCall = composition.IndexOf(
            "await notifications.ShowDataRecoveryNoticeAsync(outcome, token);",
            StringComparison.Ordinal);
        var observed = composition.IndexOf(
            "\"data-recovery-notice\",",
            StringComparison.Ordinal);

        Assert.True(safeModeReturn >= 0);
        Assert.True(recoveryCheck > safeModeReturn);
        Assert.True(observed > recoveryCheck);
        Assert.True(registerCall > observed);
        Assert.True(showCall > registerCall);
    }

    [Fact]
    public void Pause_state_changes_reconcile_the_pause_gate_instead_of_writing_desired_visible()
    {
        // Finding 1 (BLOCKER): applyPauseAsync used to call
        // runtime.SetUserVisibleAsync(state.Mode == PauseMode.None, token),
        // writing the DESIRED-visible flag directly instead of just
        // re-evaluating the pause gate. A timed pause (e.g. "take a
        // five-minute break") set _userVisible to false when it started,
        // but nothing ever set it back to true when the pause expired --
        // OnPauseStateChangedAsync (the actual pause-gate reconciler) only
        // vetoes overlay Show/Hide, it never restores _userVisible for
        // her -- so the pet stayed permanently hidden once the timer ran
        // out. Fixed by calling runtime.OnPauseStateChangedAsync instead,
        // which leaves _userVisible untouched and only re-applies the
        // pause gate against whatever it currently is.
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionBootstrap.cs"));

        var applyPauseStart = composition.IndexOf(
            "applyPauseAsync: async (state, token) =>", StringComparison.Ordinal);
        Assert.True(applyPauseStart >= 0, "Expected an applyPauseAsync composition site.");
        var applyPauseEnd = composition.IndexOf("},", applyPauseStart, StringComparison.Ordinal);
        Assert.True(applyPauseEnd > applyPauseStart, "Expected the applyPauseAsync lambda to close.");
        var applyPauseBody = composition[applyPauseStart..applyPauseEnd];

        Assert.Contains("await runtime.OnPauseStateChangedAsync(token);", applyPauseBody);
        Assert.DoesNotContain("runtime.SetUserVisibleAsync(", applyPauseBody);

        // The delegating method itself must forward to the lifecycle's
        // pause-gate reconciler, not to SetUserVisibleAsync.
        Assert.Contains(
            "public Task OnPauseStateChangedAsync(CancellationToken cancellationToken = default) =>",
            runtime);
        Assert.Contains("_lifecycle.OnPauseStateChangedAsync(cancellationToken);", runtime);
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
