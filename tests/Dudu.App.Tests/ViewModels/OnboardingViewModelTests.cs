using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task Onboarding_has_three_steps_name_then_look_then_pairing()
    {
        await using var fixture = OnboardingFixture.Create();
        var vm = fixture.ViewModel;

        Assert.Equal(3, OnboardingViewModel.StepCount);
        Assert.Equal(
            [OnboardingStep.Recipient, OnboardingStep.Appearance, OnboardingStep.Pairing],
            Enum.GetValues<OnboardingStep>());
        Assert.Equal(OnboardingStep.Recipient, vm.CurrentStep);
        Assert.Equal("step 1 of 3", vm.ProgressText);

        vm.RecipientName = "Mia";
        Assert.True(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.Appearance, vm.CurrentStep);
        Assert.Equal("step 2 of 3", vm.ProgressText);

        Assert.True(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.Pairing, vm.CurrentStep);
        Assert.Equal("step 3 of 3", vm.ProgressText);
        Assert.Empty(fixture.Events);

        vm.SkipPairing();
        Assert.True(await vm.NextAsync(fixture.CancellationToken));
        Assert.True(vm.IsComplete);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Completion_saves_startup_and_fullscreen_on_and_writes_nothing_reminder_related()
    {
        // Launch at sign-in and hide-during-fullscreen are not asked during
        // onboarding any more: they are saved on (the long-standing defaults)
        // and changed later in Settings. Everything else in the row (the
        // dormant reminder flags included) is left exactly as it was.
        var initial = new Preferences(
            AppTheme.System,
            false,
            false,
            false,
            false,
            TimeSpan.FromMinutes(15));
        await using var fixture = OnboardingFixture.Create(initialPreferences: initial);
        fixture.ViewModel.RecipientName = "Mia";
        fixture.ViewModel.Theme = AppTheme.Dark;
        fixture.ViewModel.ReducedMotion = true;

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        var saved = fixture.SavedPreferences!;
        Assert.True(saved.LaunchAtSignIn);
        Assert.True(saved.HidePetDuringFullscreen);
        Assert.Equal(AppTheme.Dark, saved.Theme);
        Assert.True(saved.ReducedMotion);
        Assert.Equal(
            initial with
            {
                Theme = AppTheme.Dark,
                ReducedMotion = true,
                LaunchAtSignIn = true,
                HidePetDuringFullscreen = true,
            },
            saved);
        Assert.Equal("Mia", fixture.SavedProfile!.RecipientName);
        Assert.Equal("MONITOR-2", Assert.Single(fixture.SavedPlacements).MonitorDeviceName);
    }

    [Fact]
    public async Task Completion_writes_profile_and_preferences_in_one_transaction()
    {
        await using var fixture = OnboardingFixture.Create();
        fixture.ViewModel.RecipientName = "Mia";
        fixture.ViewModel.SkipPairing();

        var completed = await fixture.ViewModel.CompleteAsync(fixture.CancellationToken);

        Assert.True(completed);
        Assert.Equal("Mia", fixture.SavedProfile!.RecipientName);
        Assert.True(fixture.SavedProfile.OnboardingComplete);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.True(fixture.SavedPreferences!.LaunchAtSignIn);
        Assert.True(fixture.SavedPreferences.HidePetDuringFullscreen);
        Assert.Single(fixture.SavedPlacements);
        Assert.Equal("MONITOR-2", fixture.SavedPlacements[0].MonitorDeviceName);
        Assert.Equal(new[] { "commit", "runtime" }, fixture.Events);
    }

    [Fact]
    public async Task Failed_completion_keeps_onboarding_incomplete_and_restores_startup()
    {
        await using var fixture = OnboardingFixture.Create(failCommit: true);
        fixture.ViewModel.RecipientName = "Mia";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        Assert.False(fixture.ViewModel.IsComplete);
        Assert.False(fixture.Startup.IsEnabled);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.Null(fixture.SavedProfile);
        Assert.Null(fixture.SavedPreferences);
        Assert.Empty(fixture.SavedPlacements);
    }

    [Fact]
    public async Task Runtime_apply_happens_after_commit_and_does_not_fake_rollback_on_failure()
    {
        var runtimeEvents = new List<string>();
        await using var fixture = OnboardingFixture.Create(
            runtimeApplier: (_, _, _) =>
            {
                runtimeEvents.Add("runtime");
                throw new InvalidOperationException("simulated runtime failure");
            });
        fixture.ViewModel.RecipientName = "Mia";

        var completed = await fixture.ViewModel.CompleteAsync(fixture.CancellationToken);

        Assert.True(completed);
        Assert.True(fixture.ViewModel.IsComplete);
        Assert.NotNull(fixture.ViewModel.RuntimeApplyError);
        Assert.NotNull(fixture.SavedProfile);
        Assert.NotNull(fixture.SavedPreferences);
        Assert.NotEmpty(fixture.SavedPlacements);
        Assert.Equal(new[] { "commit" }, fixture.Events);
        Assert.Equal(new[] { "runtime" }, runtimeEvents);
    }

    [Fact]
    public async Task Concurrent_completion_commits_and_applies_only_once()
    {
        await using var fixture = OnboardingFixture.Create();
        fixture.ViewModel.RecipientName = "Mia";

        var results = await Task.WhenAll(
            fixture.ViewModel.CompleteAsync(fixture.CancellationToken),
            fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        Assert.All(results, Assert.True);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.Equal(1, fixture.RuntimeApplyCount);
    }

    [Fact]
    public async Task Completion_persists_and_applies_current_dragged_monitor_placement()
    {
        var captured = new MonitorPlacementSnapshot(
            new PetPlacement("MONITOR-CURRENT", 0.17, 0.63, 1.25),
            new PixelRect(2100, 140, 640, 640),
            new MonitorInfo("MONITOR-CURRENT", new PixelRect(1920, 0, 1920, 1040), 144));
        await using var fixture = OnboardingFixture.Create(
            initialPlacement: new PetPlacement("MONITOR-STALE", 0.8, 0.8, 1),
            placementCapture: _ => Task.FromResult(captured));
        fixture.ViewModel.RecipientName = "Mia";

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        Assert.Equal(captured.Placement, fixture.SavedPlacements.Single());
        Assert.Equal(captured.Placement, fixture.AppliedPlacement);
    }

    [Fact]
    public async Task Completion_passes_reduced_motion_to_the_live_animation_applier()
    {
        var reducedMotionModes = new List<bool>();
        await using var fixture = OnboardingFixture.Create(
            runtimeApplier: (preferences, _, _) =>
            {
                reducedMotionModes.Add(preferences.ReducedMotion);
                return Task.CompletedTask;
            });
        fixture.ViewModel.RecipientName = "Mia";
        fixture.ViewModel.ReducedMotion = true;

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        Assert.Equal(new[] { true }, reducedMotionModes);
        Assert.True(fixture.SavedPreferences!.ReducedMotion);
    }

    [Fact]
    public async Task Startup_retry_clears_only_startup_error_and_preserves_live_runtime_error()
    {
        await using var fixture = OnboardingFixture.Create(
            runtimeApplier: (_, _, _) => throw new InvalidOperationException("live apply failed"));
        fixture.StartupWriter.FailWrite = true;
        fixture.ViewModel.RecipientName = "Mia";

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));
        Assert.NotNull(fixture.ViewModel.StartupRegistrationError);
        Assert.NotNull(fixture.ViewModel.RuntimeApplyError);
        Assert.True(fixture.StartupSettings.NeedsReconciliation);

        fixture.StartupWriter.FailWrite = false;
        await fixture.ViewModel.RetryStartupRegistrationAsync(fixture.CancellationToken);

        Assert.Null(fixture.ViewModel.StartupRegistrationError);
        Assert.NotNull(fixture.ViewModel.RuntimeApplyError);
        Assert.False(fixture.StartupSettings.NeedsReconciliation);
    }

    [Fact]
    public async Task Shared_preference_ownership_preserves_startup_retry_state()
    {
        await using var fixture = OnboardingFixture.Create(
            runtimeApplier: (_, _, _) => throw new InvalidOperationException("live apply failed"));
        fixture.StartupWriter.FailWrite = true;
        fixture.ViewModel.RecipientName = "Mia";

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));
        Assert.True(fixture.StartupSettings.NeedsReconciliation);
        Assert.True(fixture.StartupSettings.DesiredLaunchAtSignIn);
        Assert.NotNull(fixture.StartupSettings.ReconciliationError);
        Assert.NotNull(fixture.ViewModel.RuntimeApplyError);

        fixture.StartupWriter.FailWrite = false;
        await fixture.ViewModel.RetryStartupRegistrationAsync(fixture.CancellationToken);

        Assert.False(fixture.StartupSettings.NeedsReconciliation);
        Assert.Null(fixture.StartupSettings.ReconciliationError);
        Assert.Null(fixture.ViewModel.StartupRegistrationError);
        Assert.NotNull(fixture.ViewModel.RuntimeApplyError);
    }

    [Fact]
    public async Task Onboarding_true_replaces_failed_precompletion_false_reconciliation_target()
    {
        await using var fixture = OnboardingFixture.Create();
        await fixture.Startup.SetEnabledAsync(true, fixture.CancellationToken);
        fixture.StartupWriter.FailDelete = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.StartupSettings.ReconcileExternalAsync(false, fixture.CancellationToken));
        Assert.False(fixture.StartupSettings.DesiredLaunchAtSignIn);
        fixture.StartupWriter.FailDelete = false;
        fixture.ViewModel.RecipientName = "Mia";

        Assert.True(await fixture.ViewModel.CompleteAsync(fixture.CancellationToken));

        Assert.True(fixture.Startup.IsEnabled);
        Assert.True(fixture.StartupSettings.Current.LaunchAtSignIn);
        Assert.True(fixture.SavedPreferences!.LaunchAtSignIn);
        Assert.Equal(fixture.StartupSettings.Current, fixture.SavedPreferences);
        Assert.False(fixture.StartupSettings.NeedsReconciliation);
        Assert.Null(fixture.StartupSettings.ReconciliationError);
        Assert.Null(fixture.ViewModel.StartupRegistrationError);
    }

    [Fact]
    public async Task Load_after_setup_opens_settings_without_probing_the_relay()
    {
        // Every settings open runs LoadAsync. A live relay probe there made a
        // relay hiccup read as "aiyo couldnt load settings" long after setup.
        var pairing = new ThrowingPairingService(new InvalidOperationException("relay broke"));
        await using var fixture = OnboardingFixture.Create(pairing: pairing);
        fixture.Profiles.Restore(new Profile("Mia", OnboardingComplete: true));

        await fixture.ViewModel.LoadAsync(fixture.CancellationToken);

        Assert.True(fixture.ViewModel.IsComplete);
        Assert.Equal("Mia", fixture.ViewModel.RecipientName);
        Assert.Equal(0, pairing.StateCalls);
    }

    [Fact]
    public async Task Load_during_setup_survives_a_failing_pairing_probe()
    {
        var pairing = new ThrowingPairingService(new InvalidOperationException("relay broke"));
        await using var fixture = OnboardingFixture.Create(pairing: pairing);

        await fixture.ViewModel.LoadAsync(fixture.CancellationToken);

        Assert.Equal(1, pairing.StateCalls);
        Assert.False(fixture.ViewModel.IsComplete);
        Assert.Equal(PairingAvailability.Offline, fixture.ViewModel.PairingAvailability);
        Assert.True(fixture.ViewModel.PairingSkipped);
    }

    [Fact]
    public async Task Load_during_setup_still_honours_cancellation()
    {
        var pairing = new ThrowingPairingService(new OperationCanceledException());
        await using var fixture = OnboardingFixture.Create(pairing: pairing);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ViewModel.LoadAsync(fixture.CancellationToken));
    }

    private sealed class OnboardingFixture : IAsyncDisposable
    {
        private OnboardingFixture(
            bool failCommit,
            Func<Preferences, PetPlacement, CancellationToken, Task>? runtimeApplier,
            PetPlacement? initialPlacement,
            List<string>? events,
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture,
            IPairingService? pairing,
            Preferences? initialPreferences)
        {
            Preferences = new RecordingPreferencesRepository();
            Profiles = new RecordingProfileRepository();
            Placements = new RecordingPlacementRepository();
            Events = events ?? [];
            UnitOfWork = new RecordingUnitOfWork(Profiles, Preferences, Placements, failCommit, Events);
            StartupWriter = new RecordingStartupWriter();
            Startup = new StartupRegistrationService(
                "/opt/dudu/Dudu.exe",
                Path.Combine(Path.GetTempPath(), "dudu-onboarding-" + Guid.NewGuid().ToString("N")),
                StartupWriter);
            StartupSettings = new StartupSettingsService(
                Startup,
                new PreferenceMutationCoordinator(
                    initialPreferences ?? new Preferences(
                    AppTheme.System,
                    false,
                    true,
                    false,
                    true,
                    TimeSpan.FromMinutes(15)),
                    Preferences));
            var applyRuntime = runtimeApplier ?? ((_, _, _) =>
            {
                Events.Add("runtime");
                AppliedPlacement = SavedPlacements.Single();
                RuntimeApplyCount++;
                return Task.CompletedTask;
            });
            ViewModel = new OnboardingViewModel(
                StartupSettings.PreferenceMutations,
                Profiles,
                Placements,
                UnitOfWork,
                StartupSettings,
                pairing ?? new OfflinePairingService(),
                initialPlacement: initialPlacement ?? new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
                runtimeApplier: applyRuntime,
                placementCapture: placementCapture ?? (_ => Task.FromResult(
                    new MonitorPlacementSnapshot(
                        initialPlacement ?? new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
                        new PixelRect(0, 0, 100, 100),
                        new MonitorInfo(
                            initialPlacement?.MonitorDeviceName ?? "MONITOR-2",
                            new PixelRect(0, 0, 1920, 1040),
                            96,
                            true)))));
        }

        public RecordingPreferencesRepository Preferences { get; }
        public RecordingProfileRepository Profiles { get; }
        public RecordingPlacementRepository Placements { get; }
        public RecordingUnitOfWork UnitOfWork { get; }
        public StartupRegistrationService Startup { get; }
        public RecordingStartupWriter StartupWriter { get; }
        public StartupSettingsService StartupSettings { get; }
        public OnboardingViewModel ViewModel { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;
        public Profile? SavedProfile => Profiles.Saved;
        public Preferences? SavedPreferences => Preferences.Saved;
        public IReadOnlyList<PetPlacement> SavedPlacements => Placements.Saved;
        public List<string> Events { get; }
        public int RuntimeApplyCount { get; private set; }
        public PetPlacement? AppliedPlacement { get; private set; }

        public static OnboardingFixture Create(
            bool failCommit = false,
            Func<Preferences, PetPlacement, CancellationToken, Task>? runtimeApplier = null,
            PetPlacement? initialPlacement = null,
            List<string>? events = null,
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null,
            IPairingService? pairing = null,
            Preferences? initialPreferences = null) =>
            new(failCommit, runtimeApplier, initialPlacement, events, placementCapture, pairing, initialPreferences);

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            await Startup.DisposeAsync();
        }
    }

    private sealed class RecordingStartupWriter : IStartupLinkWriter
    {
        public bool FailWrite { get; set; }
        public bool FailDelete { get; set; }

        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("simulated startup write failure");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDelete) throw new IOException("simulated startup delete failure");
            return Task.CompletedTask;
        }
    }

    private sealed class OfflinePairingService : IPairingService
    {
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingAvailability.Offline);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingPairingService(Exception failure) : IPairingService
    {
        public int StateCalls { get; private set; }
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default)
        {
            StateCalls++;
            return Task.FromException<PairingAvailability>(failure);
        }
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingPreferencesRepository : IPreferencesRepository
    {
        public Preferences? Saved { get; private set; }
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken) { Saved = preferences; return Task.CompletedTask; }
        public void Restore(Preferences? preferences) => Saved = preferences;
    }

    private sealed class RecordingProfileRepository : IProfileRepository
    {
        public Profile? Saved { get; private set; }
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) { Saved = profile; return Task.CompletedTask; }
        public void Restore(Profile? profile) => Saved = profile;
    }

    private sealed class RecordingPlacementRepository : IPetPlacementRepository
    {
        public List<PetPlacement> Saved { get; } = [];
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.FromResult<PetPlacement?>(null);
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PetPlacement>>(Saved);
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken) { Saved.Add(placement); return Task.CompletedTask; }
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.CompletedTask;

        public List<PetPlacement> Snapshot() => [.. Saved];
        public void Restore(IEnumerable<PetPlacement> snapshot)
        {
            Saved.Clear();
            Saved.AddRange(snapshot);
        }
    }

    private sealed class RecordingUnitOfWork(
        RecordingProfileRepository profiles,
        RecordingPreferencesRepository preferences,
        RecordingPlacementRepository placements,
        bool failCommit,
        List<string> events) : IAppUnitOfWork
    {
        public int CommitCount { get; private set; }

        public async Task ExecuteAsync(Func<IAppUnitOfWorkContext, CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            CommitCount++;
            var profile = profiles.Saved;
            var savedPreferences = preferences.Saved;
            var placementsSnapshot = placements.Snapshot();
            try
            {
                await action(new RecordingContext(profiles, preferences, placements), cancellationToken);
                if (failCommit) throw new InvalidOperationException("simulated commit failure");
                events.Add("commit");
            }
            catch
            {
                profiles.Restore(profile);
                preferences.Restore(savedPreferences);
                placements.Restore(placementsSnapshot);
                throw;
            }
        }

        public Task<TResult> ExecuteAsync<TResult>(Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingContext(
        IProfileRepository profiles,
        IPreferencesRepository preferences,
        IPetPlacementRepository placements) : IAppUnitOfWorkContext
    {
        public ILocalNoteRepository LocalNotes => throw new NotSupportedException();
        public IPetPlacementRepository PetPlacements => placements;
        public IPreferencesRepository Preferences => preferences;
        public IProfileRepository Profiles => profiles;
        public IRemoteEnvelopeRepository RemoteEnvelopes => throw new NotSupportedException();
    }
}
