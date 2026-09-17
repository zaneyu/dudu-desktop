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
    public void Reminder_uses_the_real_note_arrival_pose_instead_of_a_missing_clip()
    {
        var machine = PetStateMachine.CreateIdle();

        var result = machine.Handle(new PetEvent.ReminderDue("medicine"));

        Assert.Equal(PetState.Reminder, result.State);
        Assert.Equal("note-arrival", result.AnimationKey);
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
        Assert.Equal("celebrate", machine.Current.AnimationKey);
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

    [Fact]
    public void Comfort_dismiss_event_clears_the_comfort_state()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ComfortRequested());

        Assert.Equal(PetState.Comfort, machine.Current.State);

        var result = machine.Handle(new PetEvent.ComfortDismissed());

        Assert.Equal(PetState.Idle, result.State);
    }

    [Fact]
    public void Acknowledging_the_comfort_card_clears_comfort()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ComfortRequested());

        var result = machine.Handle(new PetEvent.PresentationAcknowledged());

        Assert.Equal(PetState.Idle, result.State);
    }

    [Fact]
    public void Pending_queues_are_capped_at_the_newest_fifty_and_coalesce_to_one_card()
    {
        var machine = PetStateMachine.CreateIdle();
        for (var index = 0; index < 60; index++)
        {
            machine.Handle(new PetEvent.ReminderDue($"reminder-{index}"));
        }

        Assert.Equal(50, machine.PendingCount);
        var coalesced = machine.Current;
        Assert.Equal(PetState.Reminder, coalesced.State);
        Assert.Equal("50 reminders due", coalesced.BubbleBody);

        // The oldest ten were evicted, so dismissing one is a no-op.
        Assert.Equal(
            PetState.Reminder,
            machine.Handle(new PetEvent.Dismissed("reminder-0")).State);
        Assert.Equal(50, machine.PendingCount);

        for (var index = 10; index < 60; index++)
        {
            machine.Handle(new PetEvent.Dismissed($"reminder-{index}"));
        }

        Assert.Equal(0, machine.PendingCount);
        Assert.Equal(PetState.Idle, machine.Current.State);
    }

    [Fact]
    public void Coalesced_remote_notes_report_their_count_only_when_there_are_several()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        Assert.Null(machine.Current.BubbleBody);

        machine.Handle(new PetEvent.RemoteNoteArrived("m-2"));

        Assert.Equal(PetState.RemoteNote, machine.Current.State);
        Assert.Equal("2 notes waiting", machine.Current.BubbleBody);
    }

    [Fact]
    public async Task Concurrent_reads_and_events_are_serialized_without_corrupting_state()
    {
        var machine = PetStateMachine.CreateIdle();
        var events = Enumerable.Range(0, 500)
            .Select(index => (PetEvent)new PetEvent.RemoteNoteArrived($"message-{index}"))
            .ToArray();
        var cancellationToken = TestContext.Current.CancellationToken;

        var tasks = events.Select(petEvent =>
                Task.Run(() => machine.Handle(petEvent), cancellationToken))
            .Append(Task.Run(() =>
            {
                for (var index = 0; index < 500; index++)
                {
                    _ = machine.Current;
                }
            }, cancellationToken));

        await Task.WhenAll(tasks);

        Assert.Equal(PetState.RemoteNote, machine.Current.State);
    }
}
