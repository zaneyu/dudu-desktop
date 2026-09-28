using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Regressions for first-run (onboarding) input handling: entries the page
/// used to drop, and confusing validation copy.</summary>
public sealed class OnboardingFlowRegressionTests
{
    [Fact]
    public async Task Going_back_from_pairing_keeps_the_typed_name_and_look()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        vm.RecipientName = "Mia";
        vm.ReducedMotion = true;
        await AdvanceToAsync(vm, OnboardingStep.Pairing, fixture.CancellationToken);

        vm.Back();
        Assert.Equal(OnboardingStep.Appearance, vm.CurrentStep);
        vm.Back();
        Assert.Equal(OnboardingStep.Recipient, vm.CurrentStep);
        Assert.False(vm.CanGoBack);

        Assert.Equal("Mia", vm.RecipientName);
        Assert.True(vm.ReducedMotion);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public async Task Missing_name_message_says_what_to_do()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;

        Assert.False(await vm.NextAsync(fixture.CancellationToken));

        Assert.Equal(OnboardingViewModel.RecipientNameMessage, vm.ValidationMessage);
        Assert.DoesNotContain("cannot name", vm.ValidationMessage);
    }

    private static async Task AdvanceToAsync(
        OnboardingViewModel vm,
        OnboardingStep target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vm.RecipientName)) vm.RecipientName = "Mia";
        while (vm.CurrentStep < target)
        {
            Assert.True(await vm.NextAsync(cancellationToken), vm.ValidationMessage);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture)
        {
            Startup = new StartupRegistrationService(
                "/opt/dudu/Dudu.exe",
                Path.Combine(Path.GetTempPath(), "dudu-onboarding-flow-" + Guid.NewGuid().ToString("N")),
                new NoopStartupWriter());
            var preferences = new Preferences(
                AppTheme.System,
                false,
                true,
                false,
                true,
                TimeSpan.FromMinutes(15));
            var startupSettings = new StartupSettingsService(
                Startup,
                new PreferenceMutationCoordinator(preferences, new MemoryPreferences()));
            ViewModel = new OnboardingViewModel(
                startupSettings.PreferenceMutations,
                new MemoryProfiles(),
                new MemoryPlacements(),
                new UnusedUnitOfWork(),
                startupSettings,
                new OfflinePairing(),
                initialPlacement: new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
                placementCapture: placementCapture);
        }

        public StartupRegistrationService Startup { get; }
        public OnboardingViewModel ViewModel { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public static Fixture Create(
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null) =>
            new(placementCapture);

        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            await Startup.DisposeAsync();
        }
    }

    private sealed class NoopStartupWriter : IStartupLinkWriter
    {
        public Task WriteAtomicAsync(string shortcutPath, string targetPath, string arguments, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string shortcutPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OfflinePairing : IPairingService
    {
        public Task<PairingAvailability> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingAvailability.Offline);
        public Task<PairingCodeResult> CreateCodeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PairingCodeResult.Offline);
        public Task DisconnectSenderSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceAsync(string? deviceId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryPreferences : IPreferencesRepository
    {
        private Preferences? _saved;
        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_saved);
        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            _saved = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryProfiles : IProfileRepository
    {
        private Profile? _saved;
        public Task<Profile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_saved);
        public Task SaveAsync(Profile profile, CancellationToken cancellationToken)
        {
            _saved = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryPlacements : IPetPlacementRepository
    {
        public Task<PetPlacement?> GetAsync(string monitorDeviceName, CancellationToken cancellationToken) =>
            Task.FromResult<PetPlacement?>(null);
        public Task<IReadOnlyList<PetPlacement>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PetPlacement>>([]);
        public Task SaveAsync(PetPlacement placement, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string monitorDeviceName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnusedUnitOfWork : IAppUnitOfWork
    {
        public Task ExecuteAsync(Func<IAppUnitOfWorkContext, CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("these regressions never commit");

        public Task<TResult> ExecuteAsync<TResult>(Func<IAppUnitOfWorkContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("these regressions never commit");
    }
}
