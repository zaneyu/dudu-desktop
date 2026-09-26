using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.Core.Tests.Pet;

/// <summary>Drag, petting/drink interactions, eat-together, and the bubbles
/// that go with them.</summary>
public sealed class PetInteractionStateTests
{
    [Fact]
    public void Drag_plays_the_drag_loop_over_everything_and_releases_back_to_the_latched_state()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));
        machine.Handle(new PetEvent.ComfortRequested());

        var dragging = machine.Handle(new PetEvent.DragStarted());

        Assert.Equal(PetState.Dragging, dragging.State);
        Assert.Equal("drag", dragging.AnimationKey);
        Assert.True(machine.IsDragging);
        Assert.True(machine.IsFocusActive);
        Assert.Equal(PetState.Comfort, machine.Handle(new PetEvent.DragEnded()).State);
        Assert.False(machine.IsDragging);
        Assert.Equal(PetState.Focus, machine.Handle(new PetEvent.ComfortDismissed()).State);
    }

    [Fact]
    public void Drag_end_without_a_start_is_harmless()
    {
        var machine = PetStateMachine.CreateIdle();

        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.DragEnded()).State);
    }

    [Fact]
    public void Petting_plays_the_petted_one_shot_even_during_focus_and_returns_to_focus()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));

        var petted = machine.Handle(new PetEvent.InteractionRequested("petted"));

        Assert.Equal(PetState.Interaction, petted.State);
        Assert.Equal("petted", petted.AnimationKey);
        var completion = PetEvent.CompletionForOneShot(new PetEvent.InteractionRequested("petted"), "petted");
        Assert.Equal(new PetEvent.InteractionDismissed("petted"), completion);
        var after = machine.Handle(completion);
        Assert.Equal(PetState.Focus, after.State);
        Assert.Equal("focus", after.AnimationKey);
    }

    [Fact]
    public void Drink_interaction_carries_the_drink_bubble_and_plays_during_a_meal()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.EatingStarted("meal-1"));

        var drink = machine.Handle(new PetEvent.InteractionRequested("drink"));

        Assert.Equal(PetState.Interaction, drink.State);
        Assert.Equal("drink", drink.AnimationKey);
        Assert.Equal(PetStateMachine.DrinkBubble, drink.BubbleTitle);
        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.InteractionDismissed("drink")).State);
    }

    [Fact]
    public void Interaction_is_ignored_while_paused_and_rejects_unknown_keys()
    {
        var machine = PetStateMachine.CreateIdle();
        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.InteractionRequested("sticker-003")).State);

        machine.Handle(new PetEvent.PauseRequested());

        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.InteractionRequested("petted")).State);
    }

    [Fact]
    public void Pausing_clears_an_in_flight_interaction()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.InteractionRequested("petted"));

        machine.Handle(new PetEvent.PauseRequested());
        machine.Handle(new PetEvent.ResumeRequested());

        Assert.Equal(PetState.Idle, machine.Current.State);
    }

    [Fact]
    public void Focus_shows_the_studying_bubble()
    {
        var machine = PetStateMachine.CreateIdle();

        var focus = machine.Handle(new PetEvent.FocusStarted("f-1"));

        Assert.Equal("focus", focus.AnimationKey);
        Assert.Equal(PetStateMachine.FocusBubble, focus.BubbleTitle);
    }

    [Fact]
    public void Eating_loops_the_eat_pose_with_its_bubble_and_ends_by_session_id()
    {
        var machine = PetStateMachine.CreateIdle();

        var eating = machine.Handle(new PetEvent.EatingStarted("meal-1"));

        Assert.Equal(PetState.Eating, eating.State);
        Assert.Equal("eat", eating.AnimationKey);
        Assert.Equal(PetStateMachine.EatingBubble, eating.BubbleTitle);
        Assert.True(machine.IsEatingActive);
        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.EatingEnded("other-meal")).State);
        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.EatingEnded("meal-1")).State);
        Assert.False(machine.IsEatingActive);
    }

    [Fact]
    public void Eating_holds_notes_reminders_and_ambient_back_like_focus()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.EatingStarted("meal-1"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));
        machine.Handle(new PetEvent.ReminderDue("r-1"));

        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.AmbientRequested("blink")).State);

        var afterMeal = machine.Handle(new PetEvent.EatingEnded("meal-1"));
        Assert.Equal(PetState.RemoteNote, afterMeal.State);
        Assert.Equal(PetState.Reminder, machine.Handle(new PetEvent.Dismissed("m-1")).State);
    }

    [Fact]
    public void Eating_shows_over_a_running_focus_session_and_hands_back_to_it()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));

        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.EatingStarted("meal-1")).State);
        Assert.Equal(PetState.Focus, machine.Handle(new PetEvent.EatingEnded("meal-1")).State);
    }

    [Fact]
    public void Drag_during_a_meal_returns_to_eating_on_release()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.EatingStarted("meal-1"));

        Assert.Equal("drag", machine.Handle(new PetEvent.DragStarted()).AnimationKey);
        Assert.Equal("eat", machine.Handle(new PetEvent.DragEnded()).AnimationKey);
    }

    [Fact]
    public void Tantrum_is_an_allowed_ambient_moment_with_its_bubble_but_not_during_a_meal()
    {
        var machine = PetStateMachine.CreateIdle();

        var tantrum = machine.Handle(new PetEvent.AmbientRequested("tantrum"));

        Assert.Equal(PetState.Ambient, tantrum.State);
        Assert.Equal("tantrum", tantrum.AnimationKey);
        Assert.Equal(PetStateMachine.TantrumBubble, tantrum.BubbleTitle);
        machine.Handle(new PetEvent.AmbientDismissed("tantrum"));

        machine.Handle(new PetEvent.EatingStarted("meal-1"));
        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.AmbientRequested("tantrum")).State);
    }

    [Fact]
    public void Ambient_drink_carries_the_drink_bubble()
    {
        var machine = PetStateMachine.CreateIdle();

        var drink = machine.Handle(new PetEvent.AmbientRequested("drink"));

        Assert.Equal(PetStateMachine.DrinkBubble, drink.BubbleTitle);
    }
}
