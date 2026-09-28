using Dudu.App.Overlay;
using Dudu.Core.Pet;
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
    public void Primary_actions_are_exactly_the_five_cute_buttons()
    {
        Assert.Equal(
            [
                OverlayAction.Pet,
                OverlayAction.DrinkWater,
                OverlayAction.EatTogether,
                OverlayAction.TinyHug,
                OverlayAction.BreatheWithMe,
            ],
            OverlayCommandRouter.PrimaryActions);
        Assert.Equal(Enum.GetValues<OverlayAction>(), OverlayCommandRouter.PrimaryActions);
    }

    [Fact]
    public void All_primary_actions_publish_unique_keyboard_metadata()
    {
        Assert.Equal(OverlayCommandRouter.PrimaryActions.Count, OverlayCommandRouter.AccessiblePrimaryActions.Count);
        Assert.All(OverlayCommandRouter.AccessiblePrimaryActions, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Label));
            Assert.False(string.IsNullOrWhiteSpace(action.AutomationId));
            Assert.Equal(OverlayCommandRouter.Label(action.Action), action.Label);
            Assert.Equal(OverlayCommandRouter.EquivalentSettingsDestination(action.Action), action.SettingsDestination);
        });
        Assert.Equal(
            OverlayCommandRouter.AccessiblePrimaryActions.Count,
            OverlayCommandRouter.AccessiblePrimaryActions.Select(action => action.AutomationId).Distinct().Count());
    }

    [Fact]
    public void Tiny_hug_and_breathe_keep_their_home_automation_ids_and_open_home()
    {
        // The ids they had as comfort-panel buttons, so UI tests and
        // assistive tech find the Home buttons unchanged.
        Assert.Equal("OverlayComfortActionTinyHug", OverlayCommandRouter.AutomationId(OverlayAction.TinyHug));
        Assert.Equal("OverlayComfortActionBreatheWithMe", OverlayCommandRouter.AutomationId(OverlayAction.BreatheWithMe));
        Assert.Equal("home", OverlayCommandRouter.EquivalentSettingsDestination(OverlayAction.TinyHug));
        Assert.Equal("home", OverlayCommandRouter.EquivalentSettingsDestination(OverlayAction.BreatheWithMe));
        Assert.Equal("tiny hug", OverlayCommandRouter.Label(OverlayAction.TinyHug));
        Assert.Equal("breathe with me", OverlayCommandRouter.Label(OverlayAction.BreatheWithMe));
    }

    [Fact]
    public void Every_comfort_action_has_a_label_and_opens_home()
    {
        Assert.All(Enum.GetValues<ComfortAction>(), action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(OverlayCommandRouter.ComfortLabel(action)));
            Assert.Equal("home", OverlayCommandRouter.EquivalentSettingsDestination(action));
        });
    }

    [Fact]
    public async Task Tiny_hug_plays_the_comfort_one_shot()
    {
        var presented = new List<(PetEvent Event, string DismissalId)>();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (petEvent, dismissalId, _) =>
        {
            presented.Add((petEvent, dismissalId));
            return Task.CompletedTask;
        });
        var router = new OverlayCommandRouter(context, (_, _) => Task.CompletedTask);

        await router.ExecuteAsync(OverlayAction.TinyHug, TestContext.Current.CancellationToken);

        var (petEvent, dismissalId) = Assert.Single(presented);
        Assert.IsType<PetEvent.ComfortRequested>(petEvent);
        Assert.Equal("comfort", dismissalId);
        Assert.False(router.IsBreathing);
    }

    [Fact]
    public async Task Tiny_hug_from_home_plays_the_hug_then_opens_home()
    {
        var calls = new List<string>();
        var context = OverlayTestFeatureContext.Create(presentOneShotPetAsync: (_, dismissalId, _) =>
        {
            calls.Add(dismissalId);
            return Task.CompletedTask;
        });
        var router = new OverlayCommandRouter(context, (destination, _) =>
        {
            calls.Add(destination);
            return Task.CompletedTask;
        });

        await router.ExecuteAccessibleAsync(OverlayAction.TinyHug, TestContext.Current.CancellationToken);

        Assert.Equal(["comfort", "home"], calls);
    }

    [Fact]
    public async Task Breathe_with_me_from_home_runs_the_exercise_then_opens_home()
    {
        var destinations = new List<string>();
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (destination, _) =>
            {
                destinations.Add(destination);
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);

        await router.ExecuteAccessibleAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);

        Assert.Equal(["home"], destinations);
        Assert.False(router.IsBreathing);
        Assert.Equal(BreathVisualPhase.Complete, router.ComfortPanel.Phase);
    }

    [Theory]
    [InlineData(OverlayAction.Pet)]
    [InlineData(OverlayAction.DrinkWater)]
    [InlineData(OverlayAction.EatTogether)]
    [InlineData(OverlayAction.TinyHug)]
    [InlineData(OverlayAction.BreatheWithMe)]
    public async Task Every_action_has_a_real_arm_in_both_execution_paths(OverlayAction action)
    {
        // Each switch ends in "_ => throw": a missing arm would compile and
        // only throw at runtime on Windows.
        // Breathing phases complete at once; a started meal waits for the
        // second toggle instead of ending itself in the background.
        var router = new OverlayCommandRouter(
            OverlayTestFeatureContext.Create(),
            (_, _) => Task.CompletedTask,
            (delay, token) => delay == OverlayCommandRouter.EatingDuration
                ? Task.Delay(Timeout.InfiniteTimeSpan, token)
                : Task.CompletedTask);

        await router.ExecuteAsync(action, TestContext.Current.CancellationToken);
        await router.ExecuteAccessibleAsync(action, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(OverlayCommandRouter.Label(action)));
        Assert.False(string.IsNullOrWhiteSpace(OverlayCommandRouter.AutomationId(action)));
        Assert.False(string.IsNullOrWhiteSpace(router.LabelFor(action)));
    }
}
