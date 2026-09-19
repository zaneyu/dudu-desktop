using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

public sealed class AppLifecycleCoordinatorTests
{
    [Fact]
    public async Task Unlock_welcomes_back_only_when_all_gates_are_clear()
    {
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
        var quiet = false;
        var fullscreen = false;
        var pause = PauseState.None;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var lifecycle = new AppLifecycleCoordinator(
            host,
            overlay,
            pet,
            preferences,
            () => pause,
            () => quiet,
            () => fullscreen,
            () => new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero));

        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Equal(PetState.WelcomeBack, pet.Current.State);

        pet.Handle(new PetEvent.Dismissed("welcome-back"));
        quiet = true;
        await lifecycle.OnSessionLockedAsync(cancellationToken);
        await lifecycle.OnSessionUnlockedAsync(cancellationToken);
        Assert.Equal(PetState.Idle, pet.Current.State);
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
        var overlay = new FakeOverlay { IsVisible = false };
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            new Preferences(
                AppTheme.System,
                new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
                false, 3, true, false, true, TimeSpan.FromMinutes(15)),
            openHome: _ => Task.FromException(new InvalidOperationException("home dispatch failed")),
            initialUserVisible: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.OnHotkeyAsync(TestContext.Current.CancellationToken));

        Assert.Equal("home dispatch failed", exception.Message);
        Assert.Equal(0, overlay.ShowCount);
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

    private sealed class FakeHost : IAppHostLifecycle
    {
        public Task ResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeOverlay : IOverlayLifecycle
    {
        public int HideCount { get; private set; }
        public int ShowCount { get; private set; }
        public int RestoreCount { get; private set; }
        public bool IsVisible { get; set; } = true;
        public void Show() { ShowCount++; IsVisible = true; }
        public void Hide() { HideCount++; IsVisible = false; }
        public void RestorePlacement() => RestoreCount++;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
