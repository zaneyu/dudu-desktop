using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

public sealed class GlobalHotkeyServiceTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", "Ctrl+Alt+D")]
    [InlineData("shift+win+f12", "Shift+Win+F12")]
    [InlineData("ALT + 7", "Alt+7")]
    public void Grammar_canonicalizes_valid_gestures(string input, string expected)
    {
        Assert.Equal(expected, HotkeyGesture.Parse(input).ToString());
    }

    [Theory]
    [InlineData("D")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+D+E")]
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+Mouse1")]
    public void Grammar_rejects_missing_or_ambiguous_keys(string input)
    {
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(input));
    }

    [Fact]
    public void Conflicting_registration_preserves_the_previous_registration()
    {
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native);
        service.SetGesture(HotkeyGesture.Parse("Ctrl+Alt+D"));
        native.RejectNextRegistration();

        Assert.Throws<HotkeyConflictException>(() =>
            service.SetGesture(HotkeyGesture.Parse("Ctrl+Shift+D")));

        Assert.Equal("Ctrl+Alt+D", service.CurrentGesture.ToString());
        Assert.Equal(0, native.UnregisterCount);
    }

    [Fact]
    public void Hotkey_message_dispatches_once_and_disposal_does_not_unregister_twice()
    {
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native);
        service.SetGesture(HotkeyGesture.Default);

        Assert.True(service.HandleMessage(GlobalHotkeyService.WmHotkey, GlobalHotkeyService.DefaultId));
        Assert.False(service.HandleMessage(GlobalHotkeyService.WmHotkey, 12345));
        service.Dispose();
        service.Dispose();

        Assert.Equal(1, native.UnregisterCount);
    }

    [Fact]
    public void Hotkey_message_triggers_consumer_and_can_replace_registration()
    {
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native);
        service.SetGesture(HotkeyGesture.Default);
        var triggered = 0;
        service.Triggered += (_, _) =>
        {
            triggered++;
            service.SetGesture(HotkeyGesture.Parse("Ctrl+Shift+D"));
        };

        Assert.True(service.HandleMessage(GlobalHotkeyService.WmHotkey, GlobalHotkeyService.DefaultId));
        Assert.Equal(1, triggered);
        Assert.Equal("Ctrl+Shift+D", service.CurrentGesture.ToString());
    }

    [Fact]
    public void Failed_old_unregister_attempt_unwinds_new_registration_and_preserves_previous()
    {
        var native = new FakeHotkeyNativeApi();
        using var service = new GlobalHotkeyService(native);
        service.SetGesture(HotkeyGesture.Default);
        native.RejectNextUnregistration();

        Assert.Throws<HotkeyConflictException>(() =>
            service.SetGesture(HotkeyGesture.Parse("Ctrl+Shift+D")));

        Assert.Equal("Ctrl+Alt+D", service.CurrentGesture.ToString());
        Assert.Equal(2, native.UnregisterCount);
    }

    private sealed class FakeHotkeyNativeApi : IGlobalHotkeyNativeApi
    {
        private bool _rejectNext;
        private bool _rejectNextUnregister;

        public int UnregisterCount { get; private set; }

        public void RejectNextRegistration() => _rejectNext = true;

        public void RejectNextUnregistration() => _rejectNextUnregister = true;

        public bool Register(int id, HotkeyModifiers modifiers, uint key)
        {
            if (_rejectNext)
            {
                _rejectNext = false;
                return false;
            }

            return true;
        }

        public bool Unregister(int id)
        {
            UnregisterCount++;
            if (_rejectNextUnregister)
            {
                _rejectNextUnregister = false;
                return false;
            }

            return true;
        }
    }
}
