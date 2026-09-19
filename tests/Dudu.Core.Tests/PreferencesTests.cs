using Dudu.Core.Models;
using Xunit;

namespace Dudu.Core.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public void Defaults_keep_the_pet_above_normal_windows()
    {
        Assert.True(Preferences.Default.AlwaysOnTop);
    }
}
