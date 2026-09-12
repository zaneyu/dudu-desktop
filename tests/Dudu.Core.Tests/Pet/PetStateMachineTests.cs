using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.Core.Tests.Pet;

public sealed class PetStateMachineTests
{
    [Fact]
    public void Manual_comfort_outranks_note_and_reminder()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ReminderDue("medicine"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        var result = machine.Handle(new PetEvent.ComfortRequested());

        Assert.Equal(PetState.Comfort, result.State);
        Assert.Equal("comfort-hug", result.AnimationKey);
    }

    [Fact]
    public void Ambient_event_is_discarded_while_focus_is_active()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));

        var result = machine.Handle(new PetEvent.AmbientRequested("wave"));

        Assert.Equal(PetState.Focus, result.State);
    }

    [Fact]
    public void Note_remains_pending_until_focus_ends()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        var result = machine.Handle(new PetEvent.FocusEnded("f-1"));

        Assert.Equal(PetState.RemoteNote, result.State);
        Assert.Equal("A note arrived 💌", result.BubbleTitle);
        Assert.Null(result.BubbleBody);
    }

    [Fact]
    public void Unmatched_and_duplicate_focus_end_events_are_ignored()
    {
        var machine = PetStateMachine.CreateIdle();

        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.FocusEnded("f-1")).State);

        machine.Handle(new PetEvent.FocusStarted("f-1"));
        Assert.Equal(PetState.Focus, machine.Handle(new PetEvent.FocusEnded("other")).State);
        Assert.Equal(PetState.FocusTransition, machine.Handle(new PetEvent.FocusEnded("f-1")).State);
        machine.Handle(new PetEvent.PresentationAcknowledged());

        Assert.Equal(PetState.Idle, machine.Handle(new PetEvent.FocusEnded("f-1")).State);
    }

    [Fact]
    public void Acknowledging_a_higher_priority_presentation_does_not_clear_hidden_focus_transition()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));
        machine.Handle(new PetEvent.FocusEnded("f-1"));
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        Assert.Equal(PetState.RemoteNote, machine.Handle(new PetEvent.PresentationAcknowledged()).State);

        Assert.Equal(PetState.FocusTransition, machine.Handle(new PetEvent.Dismissed("m-1")).State);
    }

    [Fact]
    public void Dismissing_a_durable_item_with_a_transient_key_does_not_clear_the_transient_state()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.WelcomeBackRequested());
        machine.Handle(new PetEvent.RemoteNoteArrived("greeting"));

        var result = machine.Handle(new PetEvent.Dismissed("greeting"));

        Assert.Equal(PetState.WelcomeBack, result.State);
        Assert.Equal("greeting", result.AnimationKey);
    }

    [Fact]
    public void Ambient_greeting_and_welcome_back_have_distinct_dismissal_transitions()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.WelcomeBackRequested());
        machine.Handle(new PetEvent.AmbientRequested("greeting"));

        var afterAmbientDismissal = machine.Handle(new PetEvent.AmbientDismissed("greeting"));

        Assert.Equal(PetState.WelcomeBack, afterAmbientDismissal.State);
        Assert.Equal(
            PetState.Idle,
            machine.Handle(new PetEvent.WelcomeBackDismissed()).State);
    }
}
