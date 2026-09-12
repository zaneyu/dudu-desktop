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

    [Fact]
    public void All_primary_and_comfort_actions_publish_unique_keyboard_metadata()
    {
        Assert.Equal(OverlayCommandRouter.PrimaryActions.Count, OverlayCommandRouter.AccessiblePrimaryActions.Count);
        Assert.Equal(ActionBubbleLayout.ComfortActions.Count, OverlayCommandRouter.AccessibleComfortActions.Count);
        Assert.All(OverlayCommandRouter.AccessiblePrimaryActions, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.AutomationId));
            Assert.Equal(OverlayCommandRouter.EquivalentSettingsDestination(action.Action), action.SettingsDestination);
        });
        Assert.All(OverlayCommandRouter.AccessibleComfortActions, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.AutomationId));
            Assert.Equal(OverlayCommandRouter.EquivalentSettingsDestination(action.Action), action.SettingsDestination);
        });
    }
}
