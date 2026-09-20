using Dudu.App.Hosting;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class AppLifecycleCoordinatorTests
{
    [Fact]
    public async Task Unlock_shows_the_overlay_during_quiet_hours_but_does_not_welcome_back()
    {
        // A presentOneShotAsync spy stands in for the composed
        // PetPresentationCoordinator.PresentOneShotAsync (see M2): the fix
        // that closed the null-fallback latch (this test used to assert the
        // pet stayed pinned on WelcomeBack — the very bug this work package
        // exists to remove) now makes even the no-delegate case self-clear
        // immediately, so a raised-events spy is what actually distinguishes
        // "welcome-back fired" from "gated by pause" going forward.
        //
        // Owner decision 3: quiet hours gate proactive PRESENTATION only
        // (the welcome-back greeting, with its audio), never the overlay
        // window's own visibility -- an unlock during quiet hours still
        // restores the overlay, it just skips the greeting. Pause is still
        // an authoritative veto over both and takes over as the gating
        // scenario below.
        var host = new FakeHost();
        var overlay = new FakeOverlay();
        var pet = PetStateMachine.CreateIdle();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false,
            3,
            true,
            false,
            true,
            TimeSpan.FromMinutes(15));
        var quiet = true;
        var fullscreen = false;
        var pause = PauseState.None;
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var requested = new List<PetEvent>();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            host,
            overlay,
            pet,
            preferences,
            () => pause,
            () => quiet,
            () => fullscreen,
            () => now,
            presentOneShotAsync: (petEvent, dismissalId, _) =>
            {
                requested.Add(petEvent);
                pet.Handle(petEvent);
                pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
                return Task.CompletedTask;
            });

        // Quiet hours throughout, not paused: unlock restores the overlay
        // but does not welcome her back.
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Empty(requested);
        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
        Assert.Equal(PetState.Idle, pet.Current.State);

        // Still paused at unlock (quiet hours unchanged): pause vetoes both
        // the overlay restore and the welcome-back.
        requested.Clear();
        pause = PausePolicy.ForOneHour(now);
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Empty(requested);
        Assert.Equal(1, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Unlock_does_not_welcome_back_when_she_hid_the_pet_before_locking()
    {
        // Finding 4: welcome used to be computed as
        // `canShow && !fullscreen && !snapshot.FullscreenHidden`, ignoring
        // snapshot.UserVisible entirely -- so the welcome-back greeting
        // (with audio) played on unlock even though she had explicitly
        // hidden Dudu before locking. welcome now derives from show, which
        // already accounts for snapshot.UserVisible.
        var overlay = new FakeOverlay();
        var pet = PetStateMachine.CreateIdle();
        var requested = new List<PetEvent>();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            pet,
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            presentOneShotAsync: (petEvent, dismissalId, _) =>
            {
                requested.Add(petEvent);
                pet.Handle(petEvent);
                pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
                return Task.CompletedTask;
            });
        var cancellationToken = TestContext.Current.CancellationToken;

        await lifecycle.SetUserVisibleAsync(false, cancellationToken);
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);

        Assert.Empty(requested);
        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Fullscreen_end_restores_visibility_without_welcome_back()
    {
        var overlay = new FakeOverlay();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isFullscreen: () => false);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(1, overlay.ShowCount);
    }

    [Fact]
    public async Task Fullscreen_end_restores_placement_but_respects_pause_gate()
    {
        var overlay = new FakeOverlay();
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => PausePolicy.ForOneHour(now),
            clock: () => now);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(0, overlay.ShowCount);
    }

    [Fact]
    public async Task Fullscreen_end_does_not_show_a_pet_that_was_hidden_before_fullscreen()
    {
        var overlay = new FakeOverlay();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)));

        await lifecycle.OnUserShowOrHideAsync(TestContext.Current.CancellationToken);
        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(2, overlay.HideCount);
        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(0, overlay.ShowCount);
    }

    [Fact]
    public async Task Open_home_callback_is_outside_lifecycle_gate()
    {
        AppLifecycleCoordinator? lifecycle = null;
        var callbackCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            new FakeOverlay(),
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            openHome: async _ =>
            {
                await lifecycle!.OnUserShowOrHideAsync();
                callbackCompleted.TrySetResult(true);
            });
        await using (lifecycle)
        {
            await lifecycle.OnHotkeyAsync(TestContext.Current.CancellationToken);
            await callbackCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Open_home_dispatch_failure_propagates_and_does_not_show_overlay()
    {
        // Finding E: the presentation-sink push used to happen right after
        // the _userVisible write, before awaiting _openHome -- so a faulting
        // _openHome propagated its exception with the sink already told
        // "visible" and no overlay ever shown. The push now happens next to
        // the Show call itself, after _openHome has already succeeded.
        var overlay = new FakeOverlay { IsVisible = false };
        var sink = new RecordingPresentationEnvironmentSink();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            openHome: _ => Task.FromException(new InvalidOperationException("home dispatch failed")),
            initialUserVisible: false,
            presentationEnvironment: sink);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.OnHotkeyAsync(TestContext.Current.CancellationToken));

        Assert.Equal("home dispatch failed", exception.Message);
        Assert.Equal(0, overlay.ShowCount);
        Assert.DoesNotContain(true, sink.UserVisiblePushes);
    }

    [Fact]
    public async Task Hotkey_skips_its_post_open_home_show_if_she_hid_while_it_ran()
    {
        // Finding 1: OnHotkeyAsync writes _userVisible=true + pushes true
        // under _gate, releases the gate, awaits _openHome (which can run
        // arbitrarily long -- it pumps the UI thread), and only then shows
        // the overlay. A tray "hide" landing during _openHome used to end
        // with the overlay shown anyway, despite _userVisible already false
        // and false already pushed to the presentation sink -- permanently
        // out of sync until some other caller happened to touch visibility
        // again.
        var overlay = new FakeOverlay { IsVisible = false };
        var openHomeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpenHome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: false,
            openHome: async _ =>
            {
                openHomeStarted.TrySetResult();
                await releaseOpenHome.Task;
            });

        var hotkey = Task.Run(() => lifecycle.OnHotkeyAsync(cancellationToken), cancellationToken);
        await openHomeStarted.Task.WaitAsync(cancellationToken);

        // A tray "hide" lands while _openHome is still running -- it needs
        // no gate the hotkey call is still holding, so it completes freely.
        await lifecycle.SetUserVisibleAsync(false, cancellationToken);

        releaseOpenHome.SetResult();
        await hotkey;

        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Hotkey_shows_the_pet_even_during_quiet_hours()
    {
        // Owner decision 1: quiet hours suppress proactive presentation
        // only. The hotkey is an explicit user gesture (also used for
        // second launch/activation) and must always show the pet.
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isQuietHours: () => true,
            initialUserVisible: false);

        await lifecycle.OnHotkeyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Tray_show_dudu_shows_the_pet_even_during_quiet_hours()
    {
        // Owner decision 1: the tray "show dudu" command is an explicit
        // user gesture and must always show the pet, unlike a proactive
        // release (welcome-back, fullscreen/pause restore).
        //
        // This is the real-world shape of the bug the coordinator flagged:
        // on a normal launch during quiet hours, _userVisible starts true
        // (the app WANTS her visible) but TryCanShow vetoed the actual
        // Show(), so the overlay itself never came up. OnUserShowOrHideAsync
        // must key off real overlay visibility, not the desired-state flag,
        // or the first tray click reads this as "already shown" and hides
        // instead.
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isQuietHours: () => true,
            initialUserVisible: true);

        await lifecycle.OnUserShowOrHideAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.Equal(0, overlay.HideCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task SetUserVisibleAsyncTrue_shows_the_pet_immediately_during_quiet_hours()
    {
        // Owner decision 3: TryCanShow has no quiet-hours term at all any
        // more -- quiet hours gates proactive presentation only, never the
        // overlay window's own visibility. The plain SetUserVisibleAsync(true)
        // API path (used by the settings window and production composition)
        // must show right away, not wait for quiet hours to end.
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isQuietHours: () => true,
            initialUserVisible: false);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Tray_show_dudu_shows_the_pet_after_a_pause_state_hide_leaves_it_hidden()
    {
        // Same inversion as above, reached through a different path: a
        // pause-state change hides the overlay (OnPauseStateChangedAsync)
        // while leaving _userVisible true, because hiding for pause is not
        // the user asking to be hidden. A tray click right after that must
        // still show the pet, not treat "desired visible but actually
        // hidden" as a request to hide.
        var overlay = new FakeOverlay { IsVisible = true };
        var flags = new PauseFlags();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => flags.State,
            initialUserVisible: true);

        flags.State = PausePolicy.ForOneHour(DateTimeOffset.UtcNow);
        await lifecycle.OnPauseStateChangedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);
        Assert.False(overlay.IsVisible);

        flags.State = PauseState.None;
        await lifecycle.OnUserShowOrHideAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Explicit_gesture_still_respects_lock_suspend_and_pause()
    {
        // Owner decision 3: quiet hours never veto the overlay's visibility
        // for any caller (see TryCanShow), but lock, suspend, and the pause
        // policy are not gestures she is actively making right now -- they
        // still gate the hotkey and tray show. isQuietHours is true
        // throughout to prove it has no bearing here -- this test
        // previously only ever exercised the pause path and passed against
        // pre-fix code that didn't check lock or suspend at all, because it
        // never actually drove them.
        var overlay = new FakeOverlay { IsVisible = false };
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var flags = new PauseFlags();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => flags.State,
            isQuietHours: () => true,
            clock: () => now,
            initialUserVisible: false);

        // Pause suppresses the explicit-gesture hotkey.
        flags.State = PausePolicy.ForOneHour(now);
        await lifecycle.OnHotkeyAsync(cancellationToken);
        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
        flags.State = PauseState.None;

        // Session lock suppresses the hotkey too.
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnHotkeyAsync(cancellationToken);
        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);

        // Suspend suppresses the hotkey too.
        await lifecycle.OnSuspendAsync(cancellationToken);
        await lifecycle.OnHotkeyAsync(cancellationToken);
        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
    }

    private sealed class PauseFlags
    {
        public PauseState State { get; set; } = PauseState.None;
    }

    [Fact]
    public async Task Fullscreen_restore_rechecks_pause_before_showing()
    {
        var overlay = new FakeOverlay();
        var firstGateCheck = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCheck = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pause = PauseState.None;
        var calls = 0;
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () =>
            {
                var observedPause = pause;
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstGateCheck.TrySetResult(true);
                    releaseFirstCheck.Task.GetAwaiter().GetResult();
                }

                return observedPause;
            },
            clock: () => now);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        // The pause policy is deliberately a synchronous production seam.
        // Run the lifecycle operation on a worker so its first policy read can
        // wait on the barrier without blocking this test's observer.
        var restore = Task.Run(
            () => lifecycle.OnFullscreenChangedAsync(
                false,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await firstGateCheck.Task.WaitAsync(TestContext.Current.CancellationToken);
        pause = PausePolicy.ForOneHour(now);
        releaseFirstCheck.TrySetResult(true);
        await restore;

        Assert.Equal(0, overlay.ShowCount);
    }

    [Fact]
    public async Task Onboarding_visibility_transition_shows_pet_but_respects_pause_gate()
    {
        var overlay = new FakeOverlay { IsVisible = false };
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var pause = PauseState.None;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause,
            clock: () => now,
            initialUserVisible: false);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);

        await lifecycle.SetUserVisibleAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);

        pause = PausePolicy.ForOneHour(now);
        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Desired_visibility_during_fullscreen_waits_until_fullscreen_ends()
    {
        var overlay = new FakeOverlay { IsVisible = false };
        var fullscreen = false;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            isFullscreen: () => fullscreen,
            initialUserVisible: false);

        fullscreen = true;
        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);

        fullscreen = false;
        await lifecycle.OnFullscreenChangedAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Unlock_routes_the_welcome_back_greeting_through_the_one_shot_path_when_composed()
    {
        // Regression: AppLifecycleCoordinator used to call pet.Handle(new
        // WelcomeBackRequested()) directly, and nothing ever raised
        // WelcomeBackDismissed for it — the pet stayed on the greeting pose
        // forever after the first unlock. Production now composes
        // presentOneShotAsync from PetPresentationCoordinator.PresentOneShotAsync,
        // which requests, plays, and dismisses in one call; this fake
        // reproduces just the "requests and dismisses" half so the test can
        // run without an animation engine.
        var host = new FakeHost();
        var overlay = new FakeOverlay();
        var pet = PetStateMachine.CreateIdle();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, true, false, true, TimeSpan.FromMinutes(15));
        var requested = new List<PetEvent>();
        await using var lifecycle = new AppLifecycleCoordinator(
            host,
            overlay,
            pet,
            preferences,
            presentOneShotAsync: (petEvent, dismissalId, _) =>
            {
                requested.Add(petEvent);
                pet.Handle(petEvent);
                pet.Handle(PetEvent.CompletionForOneShot(petEvent, dismissalId));
                return Task.CompletedTask;
            });

        await lifecycle.OnSessionLockedAsync(TestContext.Current.CancellationToken);
        await lifecycle.OnSessionUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.Single(requested);
        Assert.IsType<PetEvent.WelcomeBackRequested>(requested[0]);
        // Already self-cleared: no lingering Dismissed("welcome-back") needed.
        Assert.Equal(PetState.Idle, pet.Current.State);
    }

    [Fact]
    public async Task Turning_off_hide_during_fullscreen_while_hidden_restores_visibility()
    {
        // Regression for H2: the prior fix only reconciled on a fullscreen
        // EDGE, i.e. an actual OnFullscreenChangedAsync call. Production
        // wires that to the OS fullscreen-transition event
        // (WindowsCompanionBootstrap), which is edge-triggered and may never
        // fire again just because a setting changed — a user who disables
        // "hide during fullscreen" while still in the same fullscreen
        // session would stay hidden until an unrelated fullscreen exit
        // happens to occur, possibly never. UpdatePreferencesAsync itself
        // must now re-run the reconciliation on a true->false edge, so this
        // test drives the restore through UpdatePreferencesAsync alone —
        // unlike the earlier draft of this fix, it must NOT call
        // OnFullscreenChangedAsync a second time to make the assertion pass.
        var overlay = new FakeOverlay();
        var preferences = new Preferences(
            AppTheme.System,
            new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, true, false, true, TimeSpan.FromMinutes(15));
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            preferences,
            isFullscreen: () => true);

        await lifecycle.OnFullscreenChangedAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);
        Assert.False(overlay.IsVisible);

        // Still fullscreen per the app's own detector throughout — the
        // setting change alone, with no further fullscreen transition, must
        // bring her back.
        await lifecycle.UpdatePreferencesAsync(
            preferences with { HidePetDuringFullscreen = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.RestoreCount);
        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Vetoed_show_does_not_mark_the_pet_visible_and_reconcile_retries_once_the_veto_ends()
    {
        // Findings 3 & 4: EnsureUserVisibleAsync used to push
        // SetUserVisible(true) to the presentation environment sink before
        // its own veto check ran, so a sign-in launch while paused told
        // PresentationCoordinator the pet was visible even though the
        // overlay was never shown -- a held reminder could then animate into
        // that still-hidden window once the pause ended and have its row
        // deleted on that "successful" presentation. Nothing previously
        // re-attempted the show once the veto ended either, so the pet
        // stayed invisible for the rest of the session.
        // ReconcileVisibilityAsync (wired into AppHost's 30 s tick) now
        // retries the show and only then reports the pet visible.
        //
        // Owner decision 3: quiet hours no longer vetoes the overlay's
        // visibility at all (see TryCanShow), so this uses a pause -- a
        // veto that still exists -- as the gate that survives long enough
        // to observe the retry.
        var overlay = new FakeOverlay { IsVisible = false };
        var sink = new RecordingPresentationEnvironmentSink();
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var pause = PausePolicy.ForOneHour(now);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause,
            clock: () => now,
            initialUserVisible: false,
            presentationEnvironment: sink);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
        Assert.Equal(false, sink.LastUserVisible);

        pause = PauseState.None;
        await lifecycle.ReconcileVisibilityAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
        Assert.Equal(true, sink.LastUserVisible);
    }

    [Fact]
    public async Task Pause_expiring_inside_quiet_hours_restores_the_overlay_on_the_next_reconcile()
    {
        // Finding 3: quiet hours vetoed every restore path but never
        // actually hid a pet that was already visible -- a pause (same as
        // suspend/fullscreen) ending inside quiet hours used to strand her
        // invisible until quiet hours ended (morning), because
        // ReconcileVisibilityAsync's restore call routes through
        // EnsureUserVisibleAsync(requireStillDesired: true), which used to
        // respect quiet hours like every other non-gesture caller. That
        // restore now ignores quiet hours, the same way an explicit gesture
        // already did.
        var overlay = new FakeOverlay { IsVisible = true };
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var pause = PauseState.None;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            pauseState: () => pause,
            isQuietHours: () => true,
            clock: () => now,
            initialUserVisible: true);

        pause = PausePolicy.ForOneHour(now);
        await lifecycle.OnPauseStateChangedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, overlay.HideCount);
        Assert.False(overlay.IsVisible);

        // Quiet hours never changes (still true) -- only the pause expires.
        pause = PauseState.None;
        await lifecycle.ReconcileVisibilityAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Reconcile_never_shows_a_pet_she_explicitly_hid()
    {
        // Finding 4: reconcile must only retry a vetoed *desired-visible*
        // show, never override an explicit hide.
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: false);

        await lifecycle.ReconcileVisibilityAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Reconcile_hides_an_overlay_left_visible_by_a_missed_hide()
    {
        // Finding 2 (same root as Finding 1): ReconcileVisibilityAsync's
        // !_userVisible branch used to push false to the presentation sink
        // and return without checking whether the overlay itself was still
        // visible -- e.g. Finding 1's _openHome race (before that fix), or
        // any other site that changes _userVisible without successfully
        // hiding the overlay too. Reconcile is now authoritative in both
        // directions: a desired-hidden pet whose overlay is still visible
        // gets hidden right here, not left on screen indefinitely.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: false,
            presentationEnvironment: sink);

        // Simulate a missed hide: _userVisible is already false, but the
        // overlay itself is still showing -- exactly the state a race like
        // Finding 1's (before that fix) left behind.
        overlay.IsVisible = true;

        await lifecycle.ReconcileVisibilityAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.HideCount);
        Assert.False(overlay.IsVisible);
        Assert.Equal(false, sink.LastUserVisible);
    }

    [Fact]
    public async Task Explicit_show_is_not_vetoed_by_fullscreen_when_the_preference_is_off()
    {
        // Finding 5: EnsureUserVisibleAsync's veto used to include a bare
        // `fullscreen` term that blocked the show whenever any window was
        // fullscreen, even with HidePetDuringFullscreen off -- which
        // TryCanShow (evaluated right after) deliberately permits. TryCanShow
        // alone must decide.
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, false, TimeSpan.FromMinutes(15)),
            isFullscreen: () => true,
            initialUserVisible: false);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(1, overlay.ShowCount);
        Assert.True(overlay.IsVisible);
    }

    private sealed class FakeHost : IAppHostLifecycle
    {
        /// <summary>
        /// Finding 5 test hook: fires synchronously from inside ResumeAsync,
        /// standing in for AppHost.RunResumeTickAsync's own synchronous
        /// catch-up reminder tick -- lets a test observe exactly what the
        /// overlay/sink state is at the moment the host is resumed.
        /// </summary>
        public Action? OnResume { get; set; }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            OnResume?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOverlay : IOverlayLifecycle
    {
        public int HideCount { get; private set; }
        public int ShowCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool IsVisible { get; set; } = true;

        /// <summary>
        /// Finding C test hook: lets a test block a caller mid-RestorePlacement
        /// (synchronously, on whatever thread invokes it -- consistent with
        /// the existing pauseState-barrier technique in
        /// Fullscreen_restore_rechecks_pause_before_showing) so a second,
        /// concurrent lifecycle call can be driven to race against it while
        /// this one still holds the coordinator's visibility gate.
        /// </summary>
        public Action? OnRestorePlacement { get; set; }

        public void Show() { ShowCount++; IsVisible = true; }
        public void Hide() { HideCount++; IsVisible = false; }
        public void RestorePlacement() { OnRestorePlacement?.Invoke(); RestoreCount++; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPresentationEnvironmentSink : IPresentationEnvironmentSink
    {
        public List<bool> UserVisiblePushes { get; } = [];

        public bool? LastUserVisible => UserVisiblePushes.Count == 0 ? null : UserVisiblePushes[^1];

        /// <summary>
        /// Finding C test hook: fires synchronously, inside the coordinator's
        /// state gate, the instant a push lands -- before the caller that
        /// triggered it goes on to (possibly) wait on the separate visibility
        /// gate for the overlay Show/Hide call. Used to observe an explicit
        /// hide's write deterministically without waiting for its whole
        /// method call (which can itself be blocked on that other gate) to
        /// return.
        /// </summary>
        public Action<bool>? OnPush { get; set; }

        public void SetSessionLocked(bool locked)
        {
        }

        public void SetFullscreen(bool fullscreen)
        {
        }

        public void SetUserVisible(bool visible)
        {
            UserVisiblePushes.Add(visible);
            OnPush?.Invoke(visible);
        }
    }

    [Fact]
    public async Task Lock_then_reconcile_then_unlock_ends_with_the_sink_pushed_true()
    {
        // Finding A: SetUserVisible is a one-way latch from
        // PresentationCoordinator's point of view. OnSessionLockedAsync hides
        // the overlay directly without pushing anything to the sink; the
        // 30 s ReconcileVisibilityAsync tick is what observes "wants visible
        // but overlay actually hidden" and pushes false. Unlock must then end
        // with the sink pushed back to true, or the gateway stays convinced
        // the pet is hidden for the rest of the session.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);

        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.ReconcileVisibilityAsync(cancellationToken);
        Assert.Equal(false, sink.LastUserVisible);

        await lifecycle.OnSessionUnlockedAsync(cancellationToken);

        Assert.Equal(true, sink.LastUserVisible);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Fullscreen_enter_then_exit_ends_with_the_sink_pushed_true()
    {
        // Same one-way-latch gap as the lock scenario above, reached through
        // the fullscreen path instead.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);

        await lifecycle.OnFullscreenChangedAsync(true, cancellationToken);
        await lifecycle.ReconcileVisibilityAsync(cancellationToken);
        Assert.Equal(false, sink.LastUserVisible);

        await lifecycle.OnFullscreenChangedAsync(false, cancellationToken);

        Assert.Equal(true, sink.LastUserVisible);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Suspend_then_reconcile_then_resume_ends_with_the_sink_pushed_true()
    {
        // Same one-way-latch gap as the lock scenario above, reached through
        // the suspend/resume path instead.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);

        await lifecycle.OnSuspendAsync(cancellationToken);
        await lifecycle.ReconcileVisibilityAsync(cancellationToken);
        Assert.Equal(false, sink.LastUserVisible);

        await lifecycle.OnResumeAsync(cancellationToken);

        Assert.Equal(true, sink.LastUserVisible);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Reconcile_pushes_true_when_the_overlay_is_already_visible()
    {
        // Finding A: reconcile used to early-return here without telling the
        // sink anything once the overlay was already visible -- a missed
        // push anywhere else (any show site that forgot to push true) then
        // stayed wrong forever instead of self-healing on the very next
        // tick. The overlay being visible must always re-assert true.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);
        var pushesBeforeReconcile = sink.UserVisiblePushes.Count;

        await lifecycle.ReconcileVisibilityAsync(cancellationToken);

        Assert.True(sink.UserVisiblePushes.Count > pushesBeforeReconcile);
        Assert.Equal(true, sink.LastUserVisible);
    }

    [Fact]
    public async Task A_stale_reconcile_push_of_true_is_corrected_by_a_hide_that_lands_right_after()
    {
        // Finding 2: ReconcileVisibilityAsync used to read _userVisible and
        // _overlay.IsVisible, then push to the sink, all *outside* the
        // coordinator's state gate. That let an explicit hide's entire
        // _userVisible=false + push(false) run in between reconcile's reads
        // and its own push(true), so reconcile's stale "yes, show her" push
        // could land last and stick the gateway on true even though she was
        // actually just hidden. The fix makes the whole read-decide-push
        // sequence run inside a single _gate acquisition -- the same lock
        // the hide's own state write takes -- so the two critical sections
        // can never interleave, only sequence one after the other.
        //
        // Engineered deterministically: the sink's OnPush hook blocks
        // synchronously *while reconcile is still holding the state gate*
        // (SetUserVisible(true) is called from inside the locked section,
        // before _gate.Release()). A concurrent hide queued behind that same
        // gate cannot perform its own state write until reconcile's whole
        // decision -- including the push -- has completed and released it.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var reconcilePushedTrue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconcilePush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);
        sink.OnPush = visible =>
        {
            if (visible)
            {
                reconcilePushedTrue.TrySetResult();
                releaseReconcilePush.Task.GetAwaiter().GetResult();
            }
        };

        var reconcile = Task.Run(
            () => lifecycle.ReconcileVisibilityAsync(cancellationToken),
            cancellationToken);
        await reconcilePushedTrue.Task.WaitAsync(cancellationToken);

        // Queued behind the same gate reconcile is still holding inside the
        // blocked push above -- it cannot run its state write until
        // reconcile's own critical section (push included) has completed.
        var hide = Task.Run(
            () => lifecycle.SetUserVisibleAsync(false, cancellationToken),
            cancellationToken);

        releaseReconcilePush.SetResult();
        await reconcile;
        await hide;

        Assert.Equal(false, sink.LastUserVisible);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Suspend_pushes_false_immediately_without_a_separate_reconcile_tick()
    {
        // Finding 4: unlike a session lock (which has its own
        // SetSessionLocked suppression signal on the sink), OnSuspendAsync
        // used to push nothing at all -- only the next 30 s
        // ReconcileVisibilityAsync tick noticed the overlay was hidden and
        // corrected the sink. That left up to a 30 s window where a held
        // item could animate into an overlay that was actually hidden. The
        // fix pushes SetUserVisible(false) directly from OnSuspendAsync, with
        // no reconcile tick needed.
        var overlay = new FakeOverlay { IsVisible = true };
        var sink = new RecordingPresentationEnvironmentSink();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);

        await lifecycle.OnSuspendAsync(cancellationToken);

        Assert.Equal(false, sink.LastUserVisible);
    }

    [Fact]
    public async Task Unlock_shows_before_resuming_the_host_so_a_catch_up_tick_sees_cleared_state()
    {
        // Finding 5 (regression): AppHost.ResumeAsync can run an immediate
        // catch-up reminder tick (RunResumeTickAsync) that reconciles
        // visibility and releases the presentation gateway's queue,
        // synchronously, before returning. ResumeAndMaybeWelcomeAsync used
        // to await _host.ResumeAsync BEFORE clearing _locked/_suspended and
        // showing the overlay, so that catch-up tick's reconcile saw stale
        // locked/suspended state, vetoed, and pushed the gateway back to
        // user-hidden -- stranding a reminder that became due exactly at
        // unlock until the next periodic tick (~30 s later). By the time the
        // host is resumed, the overlay must already be shown and the sink
        // already pushed true.
        var overlay = new FakeOverlay { IsVisible = false };
        var host = new FakeHost();
        var sink = new RecordingPresentationEnvironmentSink();
        int? showCountAtResume = null;
        bool? sinkVisibleAtResume = null;
        host.OnResume = () =>
        {
            showCountAtResume = overlay.ShowCount;
            sinkVisibleAtResume = sink.LastUserVisible;
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            host,
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);

        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);

        Assert.Equal(1, showCountAtResume);
        Assert.Equal(true, sinkVisibleAtResume);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public async Task Reconcile_never_resurrects_an_explicit_hide_that_races_in_mid_flight()
    {
        // Finding C: ReconcileVisibilityAsync captures its "still wants
        // visible" snapshot, then -- before it re-checks under the gate --
        // an explicit tray/hotkey hide can land in that window. Reconcile
        // must bail on the field's live value instead of forcing it back to
        // true and showing an overlay she just asked to be hidden.
        //
        // The race is engineered deterministically: RestorePlacement (driven
        // via OnDisplayChangedAsync) blocks while holding the coordinator's
        // shared visibility gate, so a concurrent ReconcileVisibilityAsync
        // call is guaranteed to still be waiting on that same gate inside
        // EnsureUserVisibleAsync when the explicit hide -- which does not
        // need that gate for its state write -- lands and flips _userVisible
        // to false.
        var overlay = new FakeOverlay { IsVisible = false };
        var sink = new RecordingPresentationEnvironmentSink();
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        overlay.OnRestorePlacement = () =>
        {
            restoreStarted.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
        };
        var hidePushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            initialUserVisible: true,
            presentationEnvironment: sink);
        sink.OnPush = visible =>
        {
            if (!visible)
            {
                hidePushed.TrySetResult();
            }
        };

        // Occupy the visibility gate with a blocked RestorePlacement call.
        var displayChanged = Task.Run(
            () => lifecycle.OnDisplayChangedAsync(cancellationToken),
            cancellationToken);
        await restoreStarted.Task.WaitAsync(cancellationToken);

        // Start the reconcile -- it will get stuck waiting for the same
        // visibility gate inside EnsureUserVisibleAsync.
        var reconcile = Task.Run(
            () => lifecycle.ReconcileVisibilityAsync(cancellationToken),
            cancellationToken);

        // The explicit hide's own overlay.Hide call also needs the visibility
        // gate (so this whole call cannot return yet) -- but its
        // _userVisible write and sink push happen under the separate state
        // gate first, so waiting on the push alone (via the hook above)
        // observes that write deterministically without deadlocking here.
        var hide = Task.Run(
            () => lifecycle.SetUserVisibleAsync(false, cancellationToken),
            cancellationToken);
        await hidePushed.Task.WaitAsync(cancellationToken);
        Assert.Equal(false, sink.LastUserVisible);

        releaseRestore.SetResult();
        await displayChanged;
        await reconcile;
        await hide;

        // Reconcile must not have resurrected the explicit hide: no show,
        // and no stray push back to true.
        Assert.Equal(0, overlay.ShowCount);
        Assert.False(overlay.IsVisible);
        Assert.Equal(false, sink.LastUserVisible);
    }
}
