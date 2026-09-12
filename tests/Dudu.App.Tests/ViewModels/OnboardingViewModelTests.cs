using Dudu.App.System;
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
        Assert.Equal(3, fixture.SavedPreferences!.LocalNoteDailyLimit);
        Assert.Single(fixture.SavedPlacements);
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
    }

    private sealed class OnboardingFixture : IAsyncDisposable
    {
        private OnboardingFixture(bool failCommit)
        {
            Preferences = new RecordingPreferencesRepository();
            Profiles = new RecordingProfileRepository();
            Placements = new RecordingPlacementRepository();
            UnitOfWork = new RecordingUnitOfWork(Profiles, Preferences, Placements, failCommit);
            Startup = new StartupRegistrationService(
                "/opt/dudu/Dudu.exe",
                Path.Combine(Path.GetTempPath(), "dudu-onboarding-" + Guid.NewGuid().ToString("N")),
                new RecordingStartupWriter());
            ViewModel = new OnboardingViewModel(
                Preferences,
                Profiles,
                Placements,
                UnitOfWork,
                Startup,
                new OfflinePairingService());
        }

        public RecordingPreferencesRepository Preferences { get; }
        public RecordingProfileRepository Profiles { get; }
        public RecordingPlacementRepository Placements { get; }
        public RecordingUnitOfWork UnitOfWork { get; }
        public StartupRegistrationService Startup { get; }
        public OnboardingViewModel ViewModel { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;
        public Profile? SavedProfile => Profiles.Saved;
        public Preferences? SavedPreferences => Preferences.Saved;
        public IReadOnlyList<PetPlacement> SavedPlacements => Placements.Saved;

        public static OnboardingFixture Create(bool failCommit = false) => new(failCommit);

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            await Startup.DisposeAsync();
        }
    }

    private sealed class RecordingStartupWriter : IStartupLinkWriter
    {
        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    }

    private sealed class RecordingProfileRepository : IProfileRepository
    {
        public Profile? Saved { get; private set; }
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken) { Saved = profile; return Task.CompletedTask; }
    }

    private sealed class RecordingPlacementRepository : IPetPlacementRepository
    {
        public List<PetPlacement> Saved { get; } = [];
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.FromResult<PetPlacement?>(null);
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PetPlacement>>(Saved);
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken) { Saved.Add(placement); return Task.CompletedTask; }
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingUnitOfWork(
        RecordingProfileRepository profiles,
        RecordingPreferencesRepository preferences,
        RecordingPlacementRepository placements,
        bool failCommit) : IAppUnitOfWork
    {
        public int CommitCount { get; private set; }

        public async Task ExecuteAsync(Func<IAppUnitOfWorkContext, CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            CommitCount++;
            await action(new RecordingContext(profiles, preferences, placements), cancellationToken);
            if (failCommit) throw new InvalidOperationException("simulated commit failure");
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
        IPetPlacementRepository placements) : IAppUnitOfWorkContext
    {
        public ICheckInRepository CheckIns => throw new NotSupportedException();
        public ICountdownRepository Countdowns => throw new NotSupportedException();
        public IFocusSessionRepository FocusSessions => throw new NotSupportedException();
        public ILocalNoteRepository LocalNotes => throw new NotSupportedException();
        public IPetPlacementRepository PetPlacements => placements;
        public IPreferencesRepository Preferences => preferences;
        public IProfileRepository Profiles => profiles;
        public IRemoteEnvelopeRepository RemoteEnvelopes => throw new NotSupportedException();
        public IReminderRepository Reminders => throw new NotSupportedException();
        public ITaskRepository Tasks => throw new NotSupportedException();
    }
}
