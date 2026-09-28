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
        machine.Handle(new PetEvent.EatingStarted("meal-1"));
        machine.Handle(new PetEvent.ComfortRequested());

        var dragging = machine.Handle(new PetEvent.DragStarted());

        Assert.Equal(PetState.Dragging, dragging.State);
        Assert.Equal("drag", dragging.AnimationKey);
        Assert.True(machine.IsDragging);
        Assert.True(machine.IsEatingActive);
        Assert.Equal(PetState.Comfort, machine.Handle(new PetEvent.DragEnded()).State);
        Assert.False(machine.IsDragging);
        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.ComfortDismissed()).State);
    }

    [Fact]
    public void Drag_end_without_a_start_is_harmless()
    {
        var machine = PetStateMachine.CreateIdle();

        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.DragEnded()).State);
    }

    [Fact]
    public void Petting_plays_the_petted_one_shot_even_during_a_meal_and_returns_to_eating()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.EatingStarted("meal-1"));

        var petted = machine.Handle(new PetEvent.InteractionRequested("petted"));

        Assert.Equal(PetState.Interaction, petted.State);
        Assert.Equal("petted", petted.AnimationKey);
        var completion = PetEvent.CompletionForOneShot(new PetEvent.InteractionRequested("petted"), "petted");
        Assert.Equal(new PetEvent.InteractionDismissed("petted"), completion);
        var after = machine.Handle(completion);
        Assert.Equal(PetState.Eating, after.State);
        Assert.Equal("eat", after.AnimationKey);
    }

    [Theory]
    [InlineData("grumpy", PetStateMachine.GrumpyBubble)]
    [InlineData("tantrum", PetStateMachine.PokedTantrumBubble)]
    [InlineData("flail", PetStateMachine.FlailBubble)]
    [InlineData("wail", PetStateMachine.WailBubble)]
    public void Poke_reactions_play_as_interactions_with_their_own_bubble(string key, string bubble)
    {
        var machine = PetStateMachine.CreateIdle();

        var reaction = machine.Handle(new PetEvent.InteractionRequested(key));

        Assert.Equal(PetState.Interaction, reaction.State);
        Assert.Equal(key, reaction.AnimationKey);
        Assert.Equal(bubble, reaction.BubbleTitle);
        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.InteractionDismissed(key)).State);
    }

    [Fact]
    public void The_neglect_tantrum_still_asks_to_be_petted()
    {
        var machine = PetStateMachine.CreateIdle();

        var tantrum = machine.Handle(new PetEvent.AmbientRequested("tantrum"));

        Assert.Equal(PetStateMachine.TantrumBubble, tantrum.BubbleTitle);
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
    public void Studying_loops_the_focus_pose_with_its_bubble_and_ends_by_session_id()
    {
        var machine = PetStateMachine.CreateIdle();

        var studying = machine.Handle(new PetEvent.StudyStarted("study-1"));

        Assert.Equal(PetState.Studying, studying.State);
        Assert.Equal("focus", studying.AnimationKey);
        Assert.Equal(PetStateMachine.StudyingBubble, studying.BubbleTitle);
        Assert.True(machine.IsStudyingActive);
        Assert.Equal(PetState.Studying, machine.Handle(new PetEvent.StudyEnded("other-study")).State);
        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.StudyEnded("study-1")).State);
        Assert.False(machine.IsStudyingActive);
    }

    [Fact]
    public void Studying_holds_notes_and_ambient_back_and_petting_plays_over_it()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.StudyStarted("study-1"));

        Assert.Equal(PetState.Studying, machine.Handle(new PetEvent.RemoteNoteArrived("note-1")).State);
        Assert.Equal(PetState.Studying, machine.Handle(new PetEvent.AmbientRequested("blink")).State);
        Assert.Equal(PetState.Interaction, machine.Handle(new PetEvent.InteractionRequested("petted")).State);
        Assert.Equal(PetState.Studying, machine.Handle(new PetEvent.InteractionDismissed("petted")).State);
        Assert.Equal(PetState.RemoteNote, machine.Handle(new PetEvent.StudyEnded("study-1")).State);
    }

    [Fact]
    public void Singing_loops_the_sing_pose_holds_notes_back_and_ends_by_session_id()
    {
        var machine = PetStateMachine.CreateIdle();

        var singing = machine.Handle(new PetEvent.SingStarted("song-1"));

        Assert.Equal(PetState.Singing, singing.State);
        Assert.Equal("sing", singing.AnimationKey);
        Assert.Equal(PetStateMachine.SingingBubble, singing.BubbleTitle);
        Assert.True(machine.IsSingingActive);
        Assert.Equal(PetState.Singing, machine.Handle(new PetEvent.RemoteNoteArrived("note-1")).State);
        Assert.Equal(PetState.Singing, machine.Handle(new PetEvent.AmbientRequested("blink")).State);
        Assert.Equal(PetState.Singing, machine.Handle(new PetEvent.SingEnded("other-song")).State);
        Assert.Equal(PetState.RemoteNote, machine.Handle(new PetEvent.SingEnded("song-1")).State);
        Assert.False(machine.IsSingingActive);
    }

    [Fact]
    public void Eating_holds_notes_and_ambient_back()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.EatingStarted("meal-1"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        Assert.Equal(PetState.Eating, machine.Current.State);
        Assert.Equal(PetState.Eating, machine.Handle(new PetEvent.AmbientRequested("blink")).State);

        var afterMeal = machine.Handle(new PetEvent.EatingEnded("meal-1"));
        Assert.Equal(PetState.RemoteNote, afterMeal.State);
        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.Dismissed("m-1")).State);
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
