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
}
