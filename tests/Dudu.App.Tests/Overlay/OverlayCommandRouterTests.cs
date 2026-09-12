using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class OverlayCommandRouterTests
{
    [Fact]
    public void Every_primary_action_has_a_settings_destination()
    {
        Assert.All(OverlayCommandRouter.PrimaryActions, action =>
            Assert.False(string.IsNullOrWhiteSpace(OverlayCommandRouter.EquivalentSettingsDestination(action))));
    }
}
