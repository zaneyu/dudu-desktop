using Dudu.App.System;
using Xunit;

namespace Dudu.App.Tests.System;

/// <summary>Regression coverage for global-shortcut choices that would break
/// typing or Windows itself, and for held-key repeat.</summary>
public sealed class HotkeyUxTests
{
    [Theory]
    [InlineData("Shift+A")]
    [InlineData("Shift+7")]
    [InlineData("Shift+Space")]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+A")]
    [InlineData("Ctrl+S")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Space")]
    public void Shortcuts_that_would_hijack_typing_or_windows_are_refused_with_a_reason(string shortcut)
    {
        var exception = Assert.Throws<HotkeyConflictException>(() => HotkeyGesture.Parse(shortcut));

        Assert.Contains(shortcut, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt+D", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+D")]
    [InlineData("Ctrl+Shift+D")]
    [InlineData("Ctrl+Alt+C")]
    [InlineData("Shift+F9")]
    [InlineData("Ctrl+F12")]
    [InlineData("Shift+Win+D")]
    [InlineData("Alt+7")]
    public void Ordinary_companion_shortcuts_are_still_accepted(string shortcut)
    {
        Assert.Equal(shortcut, HotkeyGesture.Parse(shortcut).ToString());
    }

    [Fact]
    public void The_default_shortcut_is_never_reserved()
    {
        Assert.False(HotkeyGesture.IsReservedByWindowsOrEveryApp(
            HotkeyGesture.Default.Modifiers,
            HotkeyGesture.Default.Key));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Shift)]
    [InlineData(HotkeyModifiers.Win)]
    public void Native_registration_always_asks_windows_not_to_auto_repeat(HotkeyModifiers modifiers)
    {
        // MOD_NOREPEAT (0x4000): holding the chord must toggle the pet once,
        // not flicker it on and off for as long as the keys stay down.
        var native = WindowsGlobalHotkeyNativeApi.ToNativeModifiers(modifiers);

        Assert.Equal(0x4000u, native & 0x4000u);
        Assert.Equal((uint)modifiers, native & ~0x4000u);
    }
}
