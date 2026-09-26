using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Regressions for first-run (onboarding) input handling: entries the page
/// used to drop or coerce silently, confusing validation copy, and out-of-order
/// placement previews while the pet-size slider is dragged.</summary>
public sealed class OnboardingFlowRegressionTests
{
    [Fact]
    public async Task Unparseable_quiet_hours_text_blocks_the_step_instead_of_keeping_the_old_time()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        await AdvanceToAsync(vm, OnboardingStep.QuietHours, fixture.CancellationToken);

        Assert.False(vm.TrySetQuietHoursText("25:00", "07:00"));

        Assert.False(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.QuietHours, vm.CurrentStep);
        Assert.Equal(OnboardingViewModel.QuietHoursTimeFormatMessage, vm.ValidationMessage);
        Assert.Equal(new TimeOnly(22, 0), vm.QuietHoursStart);

        Assert.True(vm.TrySetQuietHoursText("21:30", "06:45"));
        Assert.True(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.Reminders, vm.CurrentStep);
        Assert.Equal(new TimeOnly(21, 30), vm.QuietHoursStart);
        Assert.Equal(new TimeOnly(6, 45), vm.QuietHoursEnd);
    }

    [Fact]
    public async Task A_cleared_quiet_hours_box_is_rejected_too()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        await AdvanceToAsync(vm, OnboardingStep.QuietHours, fixture.CancellationToken);

        Assert.False(vm.TrySetQuietHoursText("22:00", "   "));

        Assert.False(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingViewModel.QuietHoursTimeFormatMessage, vm.ValidationMessage);
    }

    [Fact]
    public async Task Unparseable_quiet_hours_text_does_not_block_when_quiet_hours_are_off()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        await AdvanceToAsync(vm, OnboardingStep.QuietHours, fixture.CancellationToken);

        vm.QuietHoursEnabled = false;
        vm.TrySetQuietHoursText("not a time", "");

        Assert.True(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.Reminders, vm.CurrentStep);
    }

    [Fact]
    public async Task Equal_quiet_hours_times_explain_what_is_actually_wrong()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        await AdvanceToAsync(vm, OnboardingStep.QuietHours, fixture.CancellationToken);

        Assert.True(vm.TrySetQuietHoursText("22:00", "22:00"));

        Assert.False(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingViewModel.QuietHoursSameTimeMessage, vm.ValidationMessage);
        Assert.Contains("different", vm.ValidationMessage);
    }

    [Fact]
    public async Task A_cleared_note_limit_box_is_rejected_instead_of_becoming_zero()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        await AdvanceToAsync(vm, OnboardingStep.Reminders, fixture.CancellationToken);

        vm.SetLocalNoteDailyLimitInput(double.NaN);

        Assert.Equal(3, vm.LocalNoteDailyLimit);
        Assert.False(await vm.NextAsync(fixture.CancellationToken));
        Assert.Equal(OnboardingStep.Reminders, vm.CurrentStep);
        Assert.Equal(OnboardingViewModel.LocalNoteLimitMessage, vm.ValidationMessage);

        vm.SetLocalNoteDailyLimitInput(5);
        Assert.Equal(5, vm.LocalNoteDailyLimit);
        Assert.True(await vm.NextAsync(fixture.CancellationToken));
    }

    [Fact]
    public async Task Recommended_defaults_clear_a_stale_invalid_entry_and_keep_the_name()
    {
        await using var fixture = Fixture.Create();
        var vm = fixture.ViewModel;
        vm.RecipientName = "Mia";
        vm.TrySetQuietHoursText("nope", "07:00");
        vm.SetLocalNoteDailyLimitInput(double.NaN);

        await vm.AcceptRecommendedDefaultsAsync(fixture.CancellationToken);

        Assert.Equal("Mia", vm.RecipientName);
        await AdvanceToAsync(vm, OnboardingStep.Placement, fixture.CancellationToken);
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

    [Fact]
    public async Task A_superseded_scale_preview_is_not_applied_after_the_newer_one()
    {
        var firstCapture = new TaskCompletionSource<MonitorPlacementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        var applied = new List<double>();
        var snapshot = new MonitorPlacementSnapshot(
            new PetPlacement("MONITOR-2", 0.8, 0.8, 1),
            new PixelRect(0, 0, 100, 100),
            new MonitorInfo("MONITOR-2", new PixelRect(0, 0, 1920, 1040), 96, true));
        await using var fixture = Fixture.Create(
            placementCapture: _ => Interlocked.Increment(ref captureCount) == 1
                ? firstCapture.Task
                : Task.FromResult(snapshot),
            placementPreviewer: (placement, _) =>
            {
                applied.Add(placement.Scale);
                return Task.CompletedTask;
            });
        var vm = fixture.ViewModel;

        vm.PlacementScale = 1.2;
        var older = vm.PreviewPlacementAsync(fixture.CancellationToken);
        vm.PlacementScale = 1.6;
        await vm.PreviewPlacementAsync(fixture.CancellationToken);
        firstCapture.SetResult(snapshot);
        await older;

        Assert.Equal(new[] { 1.6 }, applied);
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
        private Fixture(
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture,
            Func<PetPlacement, CancellationToken, Task>? placementPreviewer)
        {
            Startup = new StartupRegistrationService(
                "/opt/dudu/Dudu.exe",
                Path.Combine(Path.GetTempPath(), "dudu-onboarding-flow-" + Guid.NewGuid().ToString("N")),
                new NoopStartupWriter());
            var preferences = new Preferences(
                AppTheme.System,
                new QuietHours(true, new TimeOnly(22, 0), new TimeOnly(7, 0)),
                false,
                3,
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
                placementCapture: placementCapture,
                placementPreviewer: placementPreviewer);
        }

        public StartupRegistrationService Startup { get; }
        public OnboardingViewModel ViewModel { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public static Fixture Create(
            Func<CancellationToken, Task<MonitorPlacementSnapshot>>? placementCapture = null,
            Func<PetPlacement, CancellationToken, Task>? placementPreviewer = null) =>
            new(placementCapture, placementPreviewer);

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
