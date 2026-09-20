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

    [Fact]
    public void Five_minute_break_presents_the_hug_before_applying_the_pause()
    {
        // Finding 6: TakeFiveMinuteBreakAsync used to apply the pause first
        // and present the tiny-hug comfort animation second. Applying the
        // pause hides the overlay (indirectly, via the lifecycle
        // coordinator's pause gate -- see AppLifecycleCoordinator's
        // OnPauseStateChangedAsync), so the hug used to play into a window
        // that was about to disappear underneath it. The hug must present
        // first, then the pause. Asserted from source: exercising the real
        // ordering behaviorally needs a full CompanionFeatureContext, whose
        // dozen-plus repository/service dependencies are built only by the
        // off-limits FeatureViewModelTests.cs fixture in this round.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Overlay", "OverlayCommandRouter.cs"));

        var methodStart = source.IndexOf(
            "private async Task TakeFiveMinuteBreakAsync(CancellationToken cancellationToken)",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Expected TakeFiveMinuteBreakAsync to still exist.");
        var methodEnd = source.IndexOf(
            "private async Task CloseComfortAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "Expected the next method to bound the body.");
        var body = source[methodStart..methodEnd];

        var hugCall = body.IndexOf(
            "await PresentTinyHugAsync(cancellationToken);", StringComparison.Ordinal);
        var pauseCall = body.IndexOf(
            "await _context.ApplyPauseAsync(", StringComparison.Ordinal);

        Assert.True(hugCall >= 0, "Expected the hug to still be presented.");
        Assert.True(pauseCall > hugCall, "Expected the pause to be applied after the hug, not before.");
    }

    [Fact]
    public void Five_minute_break_applies_the_pause_even_if_the_hug_faults()
    {
        // Finding 5: once the hug was reordered to run before the pause
        // (see the test above), a faulting hug animation meant no break at
        // all -- ApplyPauseAsync was never reached. The pause must now be
        // unconditional: the hug call is wrapped in its own try/catch
        // (mirroring SetComfortPanel's listener-failure handling elsewhere
        // in this file) and the pause is applied regardless of whether it
        // succeeded. Asserted from source for the same reason as the test
        // above: a full CompanionFeatureContext fixture is owned by the
        // off-limits FeatureViewModelTests.cs in this round.
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Overlay", "OverlayCommandRouter.cs"));

        var methodStart = source.IndexOf(
            "private async Task TakeFiveMinuteBreakAsync(CancellationToken cancellationToken)",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Expected TakeFiveMinuteBreakAsync to still exist.");
        var methodEnd = source.IndexOf(
            "private async Task CloseComfortAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "Expected the next method to bound the body.");
        var body = source[methodStart..methodEnd];

        var tryStart = body.IndexOf("try", StringComparison.Ordinal);
        var hugCall = body.IndexOf("await PresentTinyHugAsync(cancellationToken);", StringComparison.Ordinal);
        var catchClause = body.IndexOf("catch (Exception exception)", StringComparison.Ordinal);
        var pauseCall = body.IndexOf("await _context.ApplyPauseAsync(", StringComparison.Ordinal);

        Assert.True(tryStart >= 0, "Expected the hug to be wrapped in a try block.");
        Assert.True(hugCall > tryStart, "Expected the hug call inside the try block.");
        Assert.True(catchClause > hugCall, "Expected a non-cancellation catch clause after the hug call.");
        Assert.True(
            pauseCall > catchClause,
            "Expected the pause to be applied unconditionally, after the hug's try/catch has already completed.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output path.");
    }
}
