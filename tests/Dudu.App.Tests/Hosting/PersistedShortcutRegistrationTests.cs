using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.Hosting;

/// <summary>
/// Startup used to register HotkeyGesture.Default unconditionally, so a shortcut chosen on the
/// Appearance page was lost on every restart. It now registers the persisted one and falls back
/// to the default (reported) when the stored value is unusable or already taken.
/// </summary>
public sealed class PersistedShortcutRegistrationTests
{
    [Fact]
    public void Startup_registers_the_persisted_shortcut()
    {
        var native = new ScriptedHotkeyNativeApi();
        var reporter = new RecordingErrorReporter();
        using var hotkey = new GlobalHotkeyService(native, reporter);
        hotkey.AttachOwnerWindow(42);

        var registered = PersistedHotkeyRegistration.Apply(hotkey, "Ctrl+Shift+F9", reporter);

        Assert.Equal("Ctrl+Shift+F9", registered.ToString());
        Assert.Equal("Ctrl+Shift+F9", hotkey.CurrentGesture.ToString());
        Assert.Equal([(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x78u)], native.Registrations);
        Assert.Empty(reporter.Reports);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void No_stored_shortcut_registers_the_default_without_reporting(string? stored)
    {
        var native = new ScriptedHotkeyNativeApi();
        var reporter = new RecordingErrorReporter();
        using var hotkey = new GlobalHotkeyService(native, reporter);
        hotkey.AttachOwnerWindow(42);

        var registered = PersistedHotkeyRegistration.Apply(hotkey, stored, reporter);

        Assert.Equal(HotkeyGesture.Default, registered);
        Assert.Equal(HotkeyGesture.Default, hotkey.CurrentGesture);
        Assert.Empty(reporter.Reports);
    }

    [Theory]
    [InlineData("not a shortcut")]
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+C")]
    public void An_unusable_stored_shortcut_falls_back_to_the_default_and_is_reported(string stored)
    {
        var native = new ScriptedHotkeyNativeApi();
        var reporter = new RecordingErrorReporter();
        using var hotkey = new GlobalHotkeyService(native, reporter);
        hotkey.AttachOwnerWindow(42);

        var registered = PersistedHotkeyRegistration.Apply(hotkey, stored, reporter);

        Assert.Equal(HotkeyGesture.Default, registered);
        Assert.Equal(HotkeyGesture.Default, hotkey.CurrentGesture);
        Assert.Contains(reporter.Reports, report => report.Operation == PersistedHotkeyRegistration.RestoreOperation);
    }

    [Fact]
    public void A_stored_shortcut_another_app_owns_falls_back_to_the_default_and_is_reported()
    {
        var native = new ScriptedHotkeyNativeApi();
        native.Reject(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K');
        var reporter = new RecordingErrorReporter();
        using var hotkey = new GlobalHotkeyService(native, reporter);
        hotkey.AttachOwnerWindow(42);

        var registered = PersistedHotkeyRegistration.Apply(hotkey, "Ctrl+Alt+K", reporter);

        Assert.Equal(HotkeyGesture.Default, registered);
        Assert.Equal(HotkeyGesture.Default, hotkey.CurrentGesture);
        Assert.Contains(reporter.Reports, report =>
            report.Operation == PersistedHotkeyRegistration.RestoreOperation
            && report.Exception is HotkeyConflictException);
    }

    [Fact]
    public void When_even_the_default_is_taken_the_failure_propagates_to_the_best_effort_caller()
    {
        var native = new ScriptedHotkeyNativeApi();
        native.Reject(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K');
        native.Reject(HotkeyGesture.Default.Modifiers, HotkeyGesture.Default.Key);
        var reporter = new RecordingErrorReporter();
        using var hotkey = new GlobalHotkeyService(native, reporter);
        hotkey.AttachOwnerWindow(42);

        Assert.Throws<HotkeyConflictException>(() =>
            PersistedHotkeyRegistration.Apply(hotkey, "Ctrl+Alt+K", reporter));
    }

    [Theory]
    [InlineData(null, "Ctrl+Alt+D", true)]
    [InlineData("Ctrl+Alt+K", "Ctrl+Alt+K", true)]
    [InlineData("ctrl+alt+k", "Ctrl+Alt+K", true)]
    [InlineData("Ctrl+Alt+K", "Ctrl+Alt+D", false)]
    [InlineData("garbage", "Ctrl+Alt+D", true)]
    public void Matches_compares_the_registered_gesture_with_the_stored_value(
        string? stored,
        string current,
        bool expected)
    {
        Assert.Equal(expected, PersistedHotkeyRegistration.Matches(HotkeyGesture.Parse(current), stored));
    }

    [Fact]
    public async Task ApplyThenPersist_persists_only_after_the_runtime_accepted_the_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ScriptedPreferencesRepository();
        var coordinator = new PreferenceMutationCoordinator(Preferences.Default, repository);
        var order = new List<string>();
        repository.OnSave = _ => order.Add("persist");

        var updated = await coordinator.ApplyThenPersistAsync(
            current => current with { GlobalShortcut = "Ctrl+Alt+K" },
            (_, _, _) =>
            {
                order.Add("apply");
                return Task.CompletedTask;
            },
            (_, _, _) => throw new InvalidOperationException("must not undo"),
            ct);

        Assert.Equal(["apply", "persist"], order);
        Assert.Equal("Ctrl+Alt+K", updated.GlobalShortcut);
        Assert.Equal("Ctrl+Alt+K", coordinator.Current.GlobalShortcut);
    }

    [Fact]
    public async Task ApplyThenPersist_saves_nothing_when_the_runtime_refuses()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ScriptedPreferencesRepository();
        var coordinator = new PreferenceMutationCoordinator(Preferences.Default, repository);

        await Assert.ThrowsAsync<HotkeyConflictException>(() => coordinator.ApplyThenPersistAsync(
            current => current with { GlobalShortcut = "Ctrl+Alt+K" },
            (_, _, _) => Task.FromException(new HotkeyConflictException("taken")),
            (_, _, _) => Task.CompletedTask,
            ct));

        Assert.Equal(0, repository.SaveCount);
        Assert.Null(coordinator.Current.GlobalShortcut);
    }

    [Fact]
    public async Task ApplyThenPersist_undoes_the_runtime_change_when_saving_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ScriptedPreferencesRepository
        {
            OnSave = _ => throw new IOException("disk full"),
        };
        var start = Preferences.Default with { GlobalShortcut = "Ctrl+Alt+J" };
        var coordinator = new PreferenceMutationCoordinator(start, repository);
        var undoneTo = new List<string?>();

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyThenPersistAsync(
            current => current with { GlobalShortcut = "Ctrl+Alt+K" },
            (_, _, _) => Task.CompletedTask,
            (previous, _, _) =>
            {
                undoneTo.Add(previous.GlobalShortcut);
                return Task.CompletedTask;
            },
            ct));

        Assert.Equal(["Ctrl+Alt+J"], undoneTo);
        Assert.Equal("Ctrl+Alt+J", coordinator.Current.GlobalShortcut);
    }

    private sealed class ScriptedHotkeyNativeApi : IGlobalHotkeyNativeApi
    {
        private readonly HashSet<(HotkeyModifiers, uint)> _rejected = [];

        public List<(HotkeyModifiers Modifiers, uint Key)> Registrations { get; } = [];

        public void Reject(HotkeyModifiers modifiers, uint key) => _rejected.Add((modifiers, key));

        public bool Register(int id, HotkeyModifiers modifiers, uint key)
        {
            if (_rejected.Contains((modifiers, key))) return false;
            Registrations.Add((modifiers, key));
            return true;
        }

        public bool Unregister(int id) => true;
    }

    private sealed class RecordingErrorReporter : IAppHostErrorReporter
    {
        public List<(string Operation, Exception Exception)> Reports { get; } = [];

        public void Report(string operation, Exception exception) => Reports.Add((operation, exception));
    }

    private sealed class ScriptedPreferencesRepository : IPreferencesRepository
    {
        public Action<Preferences>? OnSave { get; set; }
        public int SaveCount { get; private set; }
        public Preferences? Stored { get; private set; }

        public Task<Preferences?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

        public Task SaveAsync(Preferences preferences, CancellationToken cancellationToken)
        {
            SaveCount++;
            OnSave?.Invoke(preferences);
            Stored = preferences;
            return Task.CompletedTask;
        }
    }
}
