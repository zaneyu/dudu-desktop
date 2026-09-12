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
    public async Task Recommended_defaults_create_a_quiet_low_interruption_profile()
    {
        await using var fixture = OnboardingFixture.Create();

        await fixture.ViewModel.AcceptRecommendedDefaultsAsync(fixture.CancellationToken);

        Assert.Equal(new TimeOnly(22, 0), fixture.ViewModel.QuietHoursStart);
        Assert.Equal(new TimeOnly(7, 0), fixture.ViewModel.QuietHoursEnd);
        Assert.Equal(3, fixture.ViewModel.LocalNoteDailyLimit);
        Assert.True(fixture.ViewModel.HidePetDuringFullscreen);
        Assert.True(fixture.ViewModel.QuietHoursEnabled);
        Assert.True(fixture.ViewModel.HydrationRemindersEnabled);
        Assert.True(fixture.ViewModel.BreakRemindersEnabled);
    }

    [Fact]
    public async Task Completion_writes_profile_and_preferences_in_one_transaction()
    {
        await using var fixture = OnboardingFixture.Create();
        fixture.ViewModel.RecipientName = "Mia";
        fixture.ViewModel.SkipPairing();
        fixture.ViewModel.HydrationRemindersEnabled = false;
        fixture.ViewModel.BreakRemindersEnabled = true;

        var completed = await fixture.ViewModel.CompleteAsync(fixture.CancellationToken);

        Assert.True(completed);
        Assert.Equal("Mia", fixture.SavedProfile!.RecipientName);
        Assert.True(fixture.SavedProfile.OnboardingComplete);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
        Assert.Equal(3, fixture.SavedPreferences!.LocalNoteDailyLimit);
        Assert.False(fixture.SavedPreferences.HydrationRemindersEnabled);
        Assert.True(fixture.SavedPreferences.BreakRemindersEnabled);
        Assert.Single(fixture.SavedPlacements);
        Assert.Equal("MONITOR-2", fixture.SavedPlacements[0].MonitorDeviceName);
        Assert.Equal(2, fixture.Reminders.Saved.Count);
        Assert.Equal(
            new[] { false, true },
            fixture.Reminders.Saved.OrderBy(reminder => reminder.Id == "default-break").Select(reminder => reminder.Enabled));
        Assert.All(fixture.Reminders.Saved, reminder =>
            Assert.Equal(fixture.SavedPreferences.QuietHours, reminder.QuietHours));
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
        Assert.Empty(fixture.Reminders.Saved);
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

    private sealed class OnboardingFixture : IAsyncDisposable
    {
        private OnboardingFixture(
            bool failCommit,
            Func<Preferences, PetPlacement, CancellationToken, Task>? runtimeApplier,
            PetPlacement? initialPlacement,
            List<string>? events,
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture)
        {
            Preferences = new RecordingPreferencesRepository();
            Profiles = new RecordingProfileRepository();
            Placements = new RecordingPlacementRepository();
            Reminders = new RecordingReminderRepository();
            Events = events ?? [];
            UnitOfWork = new RecordingUnitOfWork(Profiles, Preferences, Placements, Reminders, failCommit, Events);
            StartupWriter = new RecordingStartupWriter();
            Startup = new StartupRegistrationService(
                "/opt/dudu/Dudu.exe",
                Path.Combine(Path.GetTempPath(), "dudu-onboarding-" + Guid.NewGuid().ToString("N")),
                StartupWriter);
            StartupSettings = new StartupSettingsService(
                Startup,
                Preferences,
                new Preferences(
                    AppTheme.System,
                    new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)),
                    false,
                    3,
                    true,
                    false,
                    true,
                    TimeSpan.FromMinutes(15)));
            ViewModel = new OnboardingViewModel(
                Preferences,
                Profiles,
                Placements,
                UnitOfWork,
                Startup,
                new OfflinePairingService(),
                initialPlacement: initialPlacement ?? new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
                runtimeApplier: runtimeApplier ?? ((_, _, _) =>
                {
                    Events.Add("runtime");
                    AppliedPlacement = SavedPlacements.Single();
                    RuntimeApplyCount++;
                    return Task.CompletedTask;
                }),
                placementCapture: placementCapture ?? (_ => Task.FromResult(new MonitorPlacementSnapshot(
                    initialPlacement ?? new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
                    new PixelRect(0, 0, 100, 100),
                    new MonitorInfo(
                        initialPlacement?.MonitorDeviceName ?? "MONITOR-2",
                        new PixelRect(0, 0, 1920, 1040),
                        96,
                        true)))),
                startupSettings: StartupSettings);
        }

        public RecordingPreferencesRepository Preferences { get; }
        public RecordingProfileRepository Profiles { get; }
        public RecordingPlacementRepository Placements { get; }
        public RecordingReminderRepository Reminders { get; }
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
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null) =>
            new(failCommit, runtimeApplier, initialPlacement, events, placementCapture);

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            await Startup.DisposeAsync();
        }
    }

    private sealed class RecordingStartupWriter : IStartupLinkWriter
    {
        public bool FailWrite { get; set; }

        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("simulated startup write failure");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class RecordingReminderRepository : IReminderRepository, IReminderWriter
    {
        public List<Reminder> Saved { get; } = [];
        public Task<IReadOnlyList<Reminder>> LoadDueAsync(DateTimeOffset utcNow, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Reminder>>(Saved);
        public Task RecordOccurrencesAndAdvanceAsync(Reminder reminder, IReadOnlyList<ReminderOccurrence> occurrences, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task SaveAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            Saved.RemoveAll(item => item.Id == reminder.Id);
            Saved.Add(reminder);
            return Task.CompletedTask;
        }
        public List<Reminder> Snapshot() => [.. Saved];
        public void Restore(IEnumerable<Reminder> snapshot)
        {
            Saved.Clear();
            Saved.AddRange(snapshot);
        }
    }

    private sealed class RecordingUnitOfWork(
        RecordingProfileRepository profiles,
        RecordingPreferencesRepository preferences,
        RecordingPlacementRepository placements,
        RecordingReminderRepository reminders,
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
            var remindersSnapshot = reminders.Snapshot();
            try
            {
                await action(new RecordingContext(profiles, preferences, placements, reminders), cancellationToken);
                if (failCommit) throw new InvalidOperationException("simulated commit failure");
                events.Add("commit");
            }
            catch
            {
                profiles.Restore(profile);
                preferences.Restore(savedPreferences);
                placements.Restore(placementsSnapshot);
                reminders.Restore(remindersSnapshot);
                throw;
            }
        }

        public Task<TResult> ExecuteAsync<TResult>(Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingContext(
        IProfileRepository profiles,
        IPreferencesRepository preferences,
        IPetPlacementRepository placements,
        IReminderRepository reminders) : IAppUnitOfWorkContext
    {
        public ICheckInRepository CheckIns => throw new NotSupportedException();
        public ICountdownRepository Countdowns => throw new NotSupportedException();
        public IFocusSessionRepository FocusSessions => throw new NotSupportedException();
        public ILocalNoteRepository LocalNotes => throw new NotSupportedException();
        public IPetPlacementRepository PetPlacements => placements;
        public IPreferencesRepository Preferences => preferences;
        public IProfileRepository Profiles => profiles;
        public IRemoteEnvelopeRepository RemoteEnvelopes => throw new NotSupportedException();
        public IReminderRepository Reminders => reminders;
        public ITaskRepository Tasks => throw new NotSupportedException();
    }
}
