using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests.Models;

public sealed class PreferencesGlobalShortcutTests
{
    [Fact]
    public void Default_preferences_store_no_custom_global_shortcut()
    {
        // Null means "use the built-in Ctrl+Alt+D", so existing installs keep it.
        Assert.Null(Preferences.Default.GlobalShortcut);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" Ctrl+Alt+K ", "Ctrl+Alt+K")]
    [InlineData("Ctrl+Shift+F9", "Ctrl+Shift+F9")]
    public void NormalizeGlobalShortcut_treats_blank_as_default_and_trims(string? value, string? expected)
    {
        Assert.Equal(expected, Preferences.NormalizeGlobalShortcut(value));
    }

    [Fact]
    public void Global_shortcut_takes_part_in_record_equality()
    {
        var custom = Preferences.Default with { GlobalShortcut = "Ctrl+Alt+K" };

        Assert.NotEqual(Preferences.Default, custom);
        Assert.Equal(custom, Preferences.Default with { GlobalShortcut = "Ctrl+Alt+K" });
    }
}
