using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.Core.Tests.Pet;

/// <summary>
/// Regression coverage for the display-latch fixes: one-shot completions must
/// map to canonical latch ids, unknown dismissal ids must be detectable (and
/// ignored), and acknowledgement must release a held Comfort pose.
/// </summary>
public sealed class PetDismissalRegressionTests
{
    [Fact]
    public void Completion_for_comfort_maps_to_canonical_comfort_id()
    {
        var completion = PetEvent.CompletionForOneShot(
            new PetEvent.ComfortRequested(),
            "some-unrelated-id");

        var dismissed = Assert.IsType<PetEvent.Dismissed>(completion);
        Assert.Equal("comfort", dismissed.ItemId);
    }

    [Fact]
    public void Completion_for_focus_end_maps_to_canonical_focus_end_id()
    {
        var completion = PetEvent.CompletionForOneShot(
            new PetEvent.FocusEnded("f-1"),
            "some-unrelated-id");

        var dismissed = Assert.IsType<PetEvent.Dismissed>(completion);
        Assert.Equal("focus-end", dismissed.ItemId);
    }

    [Fact]
    public void Comfort_one_shot_round_trip_returns_to_idle()
    {
        var machine = PetStateMachine.CreateIdle();
        var requested = new PetEvent.ComfortRequested();
        machine.Handle(requested);
        Assert.Equal(PetState.Comfort, machine.Current.State);

        machine.Handle(PetEvent.CompletionForOneShot(requested, "whatever"));

        Assert.Equal(PetState.Idle, machine.Current.State);
    }

    [Fact]
    public void Focus_end_one_shot_round_trip_clears_transition()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.FocusStarted("f-1"));
        var ended = new PetEvent.FocusEnded("f-1");
        machine.Handle(ended);
        Assert.Equal(PetState.FocusTransition, machine.Current.State);

        machine.Handle(PetEvent.CompletionForOneShot(ended, "whatever"));

        Assert.Equal(PetState.Idle, machine.Current.State);
    }

    [Fact]
    public void Unknown_dismissal_id_is_ignored_and_reported_as_unknown()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ComfortRequested());

        Assert.False(machine.IsKnownDismissalId("nope-missing"));
        Assert.False(machine.TryDismiss("nope-missing"));

        // The held Comfort latch is untouched by the unknown id.
        Assert.Equal(PetState.Comfort, machine.Current.State);
    }

    [Fact]
    public void Known_dismissal_ids_cover_canonical_latches()
    {
        var machine = PetStateMachine.CreateIdle();

        Assert.True(machine.IsKnownDismissalId("comfort"));
        Assert.True(machine.IsKnownDismissalId("comfort-hug"));
        Assert.True(machine.IsKnownDismissalId("focus-end"));
        Assert.True(machine.IsKnownDismissalId("focus-transition"));
        Assert.True(machine.IsKnownDismissalId("welcome-back"));
    }

    [Fact]
    public void Dismissing_a_pending_note_reports_known_and_clears()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.RemoteNoteArrived("m-1"));

        Assert.True(machine.IsKnownDismissalId("m-1"));
        Assert.True(machine.TryDismiss("m-1"));
        Assert.False(machine.IsKnownDismissalId("m-1"));
        Assert.Equal(0, machine.PendingCount);

        // The presentation refreshes on the next handled event.
        machine.Handle(new PetEvent.PresentationAcknowledged());
        Assert.Equal(PetState.Idle, machine.Current.State);
    }

    [Fact]
    public void Acknowledgement_releases_a_held_comfort_pose()
    {
        var machine = PetStateMachine.CreateIdle();
        machine.Handle(new PetEvent.ComfortRequested());
        Assert.Equal(PetState.Comfort, machine.Current.State);

        machine.Handle(new PetEvent.PresentationAcknowledged());
        machine.Handle(new PetEvent.ComfortDismissed());

        Assert.Equal(PetState.Idle, machine.Current.State);
    }
}
