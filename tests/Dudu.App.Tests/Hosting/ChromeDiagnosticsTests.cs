using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.Tray;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// P2 chrome visibility: Windows-chrome and lifecycle failures must produce
/// a diagnostic with the operation name plus the exception type through the
/// shared <see cref="IAppHostErrorReporter"/> instead of a bare
/// <c>Trace</c>. Behavior (fail-closed, best-effort, throw) is unchanged in
/// every case below.
/// </summary>
public sealed class ChromeDiagnosticsTests
{
    [Fact]
    public async Task Overlay_show_failure_reports_user_show_and_stays_hidden()
    {
        var reporter = new RecordingErrorReporter();
        var overlay = new ThrowingShowOverlay();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            initialUserVisible: false,
            errorReporter: reporter);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("user-show", report.Operation);
        Assert.IsType<InvalidOperationException>(report.Exception);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public async Task Fullscreen_probe_failure_reports_and_fails_closed_to_hidden()
    {
        var reporter = new RecordingErrorReporter();
        var overlay = new FakeOverlay();
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            overlay,
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            isFullscreen: () => throw new InvalidOperationException("probe down"),
            initialUserVisible: false,
            errorReporter: reporter);

        await lifecycle.SetUserVisibleAsync(true, TestContext.Current.CancellationToken);

        Assert.Contains(reporter.Reports, report => report.Operation == "show-fullscreen"
            && report.Exception is InvalidOperationException);
        Assert.Equal(0, overlay.ShowCount);
    }

    [Fact]
    public async Task Taskbar_tray_recreate_failure_reports_taskbar_tray_recreate()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeTrayNativeApi { ThrowOnRecreate = true };
        var tray = new TrayIconService(native, _ => { });
        tray.Attach(42);
        await using var lifecycle = new AppLifecycleCoordinator(
            new FakeHost(),
            new FakeOverlay(),
            PetStateMachine.CreateIdle(),
            TestPreferences(),
            tray: tray,
            errorReporter: reporter);
        await using (tray)
        {
            await lifecycle.OnTaskbarCreatedAsync(TestContext.Current.CancellationToken);
        }

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("taskbar-tray-recreate", report.Operation);
    }

    [Fact]
    public void Hotkey_conflict_reports_hotkey_set_gesture_and_preserves_previous()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native, reporter);

        service.SetGesture(HotkeyGesture.Parse("Ctrl+Alt+D"));
        native.RejectNextRegistration();

        var exception = Assert.Throws<HotkeyConflictException>(() =>
            service.SetGesture(HotkeyGesture.Parse("Ctrl+Shift+D")));

        Assert.Equal("Ctrl+Alt+D", service.CurrentGesture.ToString());
        Assert.Contains(reporter.Reports, report =>
            report.Operation == "hotkey-set-gesture"
            && ReferenceEquals(exception, report.Exception));
    }

    [Fact]
    public void Hotkey_owner_move_conflict_reports_hotkey_attach()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native, reporter);

        service.AttachOwnerWindow(42);
        service.SetGesture(HotkeyGesture.Default);
        native.RejectNextRegistration();

        Assert.Throws<HotkeyConflictException>(() => service.AttachOwnerWindow(43));

        Assert.Contains(reporter.Reports, report =>
            report.Operation == "hotkey-attach"
            && report.Exception is HotkeyConflictException);
    }

    [Fact]
    public void Tray_attach_failure_reports_tray_attach_and_still_throws()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeTrayNativeApi { AddResult = false };
        using var service = new TrayIconService(native, _ => { }, errorReporter: reporter);

        Assert.Throws<InvalidOperationException>(() => service.Attach(42));

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("tray-attach", report.Operation);
        Assert.IsType<InvalidOperationException>(report.Exception);
    }

    [Fact]
    public void Tray_recreate_native_failure_reports_tray_recreate_without_throwing()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeTrayNativeApi { RecreateResult = false };
        using var service = new TrayIconService(native, _ => { }, errorReporter: reporter);
        service.Attach(42);

        // Best-effort recreate stays best-effort: no throw, but no silence.
        service.Recreate();

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("tray-recreate", report.Operation);
        Assert.IsType<InvalidOperationException>(report.Exception);
    }

    [Fact]
    public void Tray_window_message_recreate_failure_reports_and_still_propagates()
    {
        var reporter = new RecordingErrorReporter();
        var native = new FakeTrayNativeApi { ThrowOnRecreate = true };
        using var service = new TrayIconService(native, _ => { }, errorReporter: reporter);
        service.Attach(42);

        Assert.Throws<InvalidOperationException>(() =>
            service.HandleWindowMessage(TrayIconService.TaskbarCreatedFallbackMessage, 0));

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("tray-recreate", report.Operation);
    }

    [Fact]
    public void Static_failure_reporter_delivers_operation_and_survives_reporter_faults()
    {
        var reporter = new RecordingErrorReporter();
        var failure = new InvalidOperationException("cleanup down");

        WindowsCompanionRuntime.ReportStaticFailure(reporter, "partial-startup-tray-cleanup", failure);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("partial-startup-tray-cleanup", report.Operation);
        Assert.Same(failure, report.Exception);

        // A faulting reporter must never break shutdown cleanup.
        WindowsCompanionRuntime.ReportStaticFailure(
            new ThrowingErrorReporter(), "runtime-lifecycle-shutdown", failure);

        // No reporter: falls back to Trace without throwing.
        WindowsCompanionRuntime.ReportStaticFailure(null, "runtime-event-shutdown", failure);
    }

    [Fact]
    public async Task Native_callback_failure_reaches_the_reporter_with_its_operation()
    {
        var reporter = new RecordingErrorReporter();
        var failure = new InvalidOperationException("tray dispatch down");

        await WindowsCompanionProductionComposition.ObserveNativeCallbackAsync(
            Task.FromException(failure),
            "tray-Exit",
            reporter);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal("tray-Exit", report.Operation);
        Assert.Same(failure, report.Exception);
    }

    [Fact]
    public async Task Native_callback_success_reports_nothing()
    {
        var reporter = new RecordingErrorReporter();

        await WindowsCompanionProductionComposition.ObserveNativeCallbackAsync(
            Task.CompletedTask,
            "tray-Exit",
            reporter);

        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public void Native_callback_operation_labels_are_preserved_in_the_event_source()
    {
        var bootstrap = ReadRepositoryFile("src", "Dudu.App", "Hosting", "WindowsCompanionBootstrap.cs");

        foreach (var operation in new[]
            {
                "session-lock", "session-unlock", "suspend", "resume",
                "display-change", "taskbar-created", "fullscreen-poll", "hotkey",
            })
        {
            Assert.Contains($"\"{operation}\"", bootstrap, StringComparison.Ordinal);
        }

        // The callback and poll failures must route through the shared
        // reporter rather than a bare Trace that a file sink would miss.
        // The old full-exception formats must be gone; the remaining Trace
        // fallbacks carry the operation name plus the exception type/HResult.
        Assert.DoesNotContain(
            "\"Dudu native callback '{0}' failed: {1}\", operation, exception",
            bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"Dudu fullscreen poll failed: {0}\"",
            bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_create_and_dispose_failures_carry_operation_names()
    {
        var overlay = ReadRepositoryFile("src", "Dudu.App", "Overlay", "OverlayWindowHost.cs");

        Assert.Contains("\"overlay-create\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"overlay-dispose\"", overlay, StringComparison.Ordinal);
        Assert.Contains("\"overlay-message-loop\"", overlay, StringComparison.Ordinal);
    }

    private static Preferences TestPreferences() => new(
        AppTheme.System,
        new QuietHours(false, TimeOnly.MinValue, TimeOnly.MinValue),
        false,
        3,
        true,
        false,
        true,
        TimeSpan.FromMinutes(15));

    private static string ReadRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var parts = new string[relativePath.Length + 1];
            parts[0] = directory.FullName;
            relativePath.CopyTo(parts, 1);
            var path = Path.Combine(parts);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }

    public sealed record ErrorReport(string Operation, Exception Exception);

    public sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        private readonly object _sync = new();

        public List<ErrorReport> Reports { get; } = [];

        public void Report(string operation, Exception exception)
        {
            lock (_sync)
            {
                Reports.Add(new ErrorReport(operation, exception));
            }
        }
    }

    private sealed class ThrowingErrorReporter : IAppHostErrorReporter
    {
        public void Report(string operation, Exception exception) =>
            throw new InvalidOperationException("reporter down");
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

    private sealed class ThrowingShowOverlay : IOverlayLifecycle
    {
        public bool IsVisible { get; private set; }
        public void Show() => throw new InvalidOperationException("show down");
        public void Hide() => IsVisible = false;
        public void RestorePlacement() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTrayNativeApi : ITrayNativeApi
    {
        public bool AddResult { get; init; } = true;
        public bool RecreateResult { get; init; } = true;
        public bool ThrowOnRecreate { get; init; }

        public bool Add(nint ownerWindow, uint callbackMessage, string tooltip) => AddResult;

        public bool Remove(nint ownerWindow) => true;

        public bool Recreate(nint ownerWindow, uint callbackMessage, string tooltip)
        {
            if (ThrowOnRecreate) throw new InvalidOperationException("recreate down");
            return RecreateResult;
        }
    }

    private sealed class FakeHotkeyNativeApi : IGlobalHotkeyNativeApi
    {
        private bool _rejectNext;

        public void RejectNextRegistration() => _rejectNext = true;

        public bool Register(int id, HotkeyModifiers modifiers, uint key)
        {
            if (_rejectNext)
            {
                _rejectNext = false;
                return false;
            }

            return true;
        }

        public bool Unregister(int id) => true;
    }
}
