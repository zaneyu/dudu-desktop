using Dudu.App.Animation;
using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Dudu.App.Overlay;
using Dudu.App.Presentation;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Abstractions;
using Dudu.Core.Assets;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hardening;

public sealed class WinUiHardeningTests
{
    [Fact]
    public async Task Dispatcher_sync_path_returns_faulted_task_instead_of_throwing()
    {
        var dispatcher = new AwaitableUiDispatcher(() => true, _ => true);
        var task = dispatcher.InvokeAsync(() => throw new InvalidOperationException("sync boom"), TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal("sync boom", exception.Message);
    }

    [Fact]
    public void PixelRect_saturates_and_never_throws()
    {
        var rect = new PixelRect(int.MaxValue - 1, 0, 10, 5);
        Assert.Equal(int.MaxValue, rect.Right);
        Assert.True(rect.Contains(int.MaxValue - 1, 0));
        var overflow = new PixelRect(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        Assert.False(overflow.Contains(int.MinValue, int.MinValue));
    }

    [Fact]
    public void HitTest_overflow_returns_non_interactive()
    {
        var pixels = new byte[16];
        Assert.False(OverlayHitTest.IsInteractive(pixels, int.MaxValue / 4, 1, int.MaxValue, 1.0, 0, 0));
        Assert.False(OverlayHitTest.IsInteractive(pixels, 1, 1, 4, double.NaN, 0, 0));
        Assert.Equal(0, OverlayHitTest.AlphaAt(pixels, 0, 0, 0, 1.0, 0, 0));
    }

    [Fact]
    public void Tray_wm_command_uses_command_count()
    {
        var native = new StubTrayNative();
        var seen = new List<TrayCommand>();
        using var service = new TrayIconService(native, seen.Add);
        service.Attach(42);
        // Index 7 == Exit (Commands.Count == 7); index 8 is out of range.
        Assert.True(service.HandleWindowMessage(0x0111, 7, 0));
        Assert.Equal([TrayCommand.Exit], seen);
        Assert.False(service.HandleWindowMessage(0x0111, 8, 0));
    }

    [Fact]
    public void Tray_off_thread_recreate_throws_instead_of_blocking()
    {
        var native = new StubTrayNative();
        using var service = new TrayIconService(native, _ => { });
        service.Attach(42);
        // Simulate a dispatcher being present but the call coming from another
        // thread: the sync path must throw, never GetResult across threads.
        var field = typeof(TrayIconService).GetField("_ownerDispatcher",
            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance)!;
        field.SetValue(service, new Func<Action, Task>(_ => Task.CompletedTask));
        var threadField = typeof(TrayIconService).GetField("_ownerThreadId",
            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance)!;
        threadField.SetValue(service, Environment.CurrentManagedThreadId + 100000);
        Assert.Throws<InvalidOperationException>(() => service.Recreate());
        Assert.Throws<InvalidOperationException>(() => service.Dispose());
    }

    [Fact]
    public async Task Tray_recreate_async_runs_inline_without_dispatcher()
    {
        var native = new StubTrayNative();
        await using var service = new TrayIconService(native, _ => { });
        service.Attach(42);
        await service.RecreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, native.RecreateCount);
    }

    [Fact]
    public void Notification_activation_constrains_ids()
    {
        Assert.NotNull(NotificationActivation.TryParse("action=reminder-done&reminderId=default-hydration"));
        Assert.Null(NotificationActivation.TryParse("action=reminder-done&reminderId=../evil"));
        Assert.Null(NotificationActivation.TryParse("action=reminder-done&reminderId=" + new string('a', 200)));
        Assert.Null(NotificationActivation.TryParse("action=reminder-done&reminderId=a/b"));
    }

    [Fact]
    public async Task Presentation_suppresses_fail_closed_on_sampler_fault()
    {
        var policy = new PresentationPolicy(TimeSpan.FromSeconds(90));
        var notifications = new StubNotifications();
        var pet = PetStateMachine.CreateIdle();
        var gate = new SemaphoreSlim(1, 1);
        var coordinator = new PresentationCoordinator(
            policy,
            notifications,
            pet,
            (_, _, _) => Task.CompletedTask,
            () => AnimationOptions.Default,
            () => false,
            () => PauseState.None,
            gate,
            isFullscreenNow: () => throw new InvalidOperationException("sampler down"));
        var item = DurableNotification.LocalNote(
            new LocalLoveNote("id1", "hello", true), "idle");
        // Fail-closed: no throw, item queued rather than presented.
        await coordinator.PublishAsync(item, bypassSuppression: false, TestContext.Current.CancellationToken);
        Assert.Equal(0, notifications.Shown);
    }

    [Fact]
    public async Task Startup_degrades_to_disabled_instead_of_throwing()
    {
        await using var service = new StartupRegistrationService(
            "/opt/Dudu.exe", string.Empty, new StubWriter());
        Assert.False(service.IsAvailable);
        Assert.False(service.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetEnabledAsync(true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Startup_failure_sets_needs_reconciliation()
    {
        await using var startup = new StartupRegistrationService(
            "/opt/Dudu.exe", "/tmp/startup-" + Guid.NewGuid().ToString("N"), new FailingWriter());
        var repository = new StubPrefsRepository();
        var preferences = new Preferences(
            AppTheme.System, new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
            false, 3, false, false, true, TimeSpan.FromMinutes(15));
        var settings = new StartupSettingsService(
            startup, new PreferenceMutationCoordinator(preferences, repository));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            settings.SetLaunchAtSignInAsync(true, TestContext.Current.CancellationToken));
        Assert.True(settings.NeedsReconciliation);
    }

    [Fact]
    public async Task Owner_queue_invocation_times_out_without_drain()
    {
        using var queue = new OwnerActionQueue(() => false, () => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Never drained and never closed: the 5s InvokeTimeout must fire.
        var task = queue.InvokeAsync(() => { }, cts.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task Single_instance_coalesces_rapid_activations()
    {
        var calls = 0;
        var transport = new FlappingTransport();
        await using var coordinator = new SingleInstanceCoordinator(
            transport, _ => { calls++; return Task.CompletedTask; });
        Assert.True(await coordinator.TryAcquireAsync(TestContext.Current.CancellationToken));
        // Two rapid payloads: the second falls inside the 250ms throttle.
        await transport.FireAsync([1]);
        await transport.FireAsync([1]);
        Assert.Equal(1, calls);
    }

    private sealed class StubTrayNative : ITrayNativeApi
    {
        public int RecreateCount { get; private set; }
        public bool Add(nint o, uint m, string t) => true;
        public bool Remove(nint o) => true;
        public bool Recreate(nint o, uint m, string t) { RecreateCount++; return true; }
    }

    private sealed class StubNotifications : INotificationService
    {
        public int Shown { get; private set; }
        public bool NotificationsAvailable => true;
        public Task ShowReminderAsync(string id, string title, CancellationToken ct)
        { Shown++; return Task.CompletedTask; }
        public Task ShowRemoteNoteArrivalAsync(Guid id, CancellationToken ct)
        { Shown++; return Task.CompletedTask; }
    }

    private sealed class StubWriter : IStartupLinkWriter
    {
        public Task WriteAtomicAsync(string s, string t, string a, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailingWriter : IStartupLinkWriter
    {
        public Task WriteAtomicAsync(string s, string t, string a, CancellationToken ct) =>
            Task.FromException(new IOException("nope"));
        public Task DeleteAsync(string s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubPrefsRepository : IPreferencesRepository
    {
        public Preferences? LastSaved { get; private set; }
        public Task<Preferences?> GetAsync(CancellationToken ct) => Task.FromResult(LastSaved);
        public Task SaveAsync(Preferences p, CancellationToken ct) { LastSaved = p; return Task.CompletedTask; }
    }

    private sealed class FlappingTransport : IActivationTransport
    {
        private Func<ReadOnlyMemory<byte>, Task>? _handler;
        public Task<bool> TryAcquirePrimaryAsync(CancellationToken ct) => Task.FromResult(true);
        public Task ListenAsync(Func<ReadOnlyMemory<byte>, Task> onPayload, CancellationToken ct)
        {
            _handler = onPayload;
            return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        public Task FireAsync(byte[] payload) =>
            _handler is null ? Task.CompletedTask : _handler(payload);
        public Task SendAsync(byte payload, TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
