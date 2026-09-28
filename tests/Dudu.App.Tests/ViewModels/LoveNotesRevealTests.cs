using Dudu.Core.Models;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Revealing a partner note reads it and keeps it: the note is saved as
/// <c>remote-&lt;messageId&gt;</c>, its envelope consumed in the same unit of work, any held
/// copy discarded, and it shows up (newest first) in the opened notes list, which lists only
/// <c>remote-</c> rows. There is no separate "save to jar" step anymore.</summary>
public sealed class LoveNotesRevealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reveal_consumes_the_envelope_and_adds_to_opened_notes()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var envelope = fixture.AddPendingEnvelope(messageId: "m1", plaintext: "hi love");
        var vm = fixture.CreateLoveNotesViewModel();
        await vm.RefreshAsync(Ct);

        await vm.RevealRemoteNoteAsync(envelope, Ct);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(["m1"], fixture.ConsumedMessageIds);
        Assert.Contains(vm.OpenedNotes, n => n.Id == "remote-m1");
        Assert.DoesNotContain(vm.PendingRemoteNotes, e => e.MessageId == "m1");
        Assert.Equal(["m1"], fixture.DiscardedHeldRemoteNoteIds);
        Assert.Equal(0, vm.UnopenedRemoteNoteCount);
        Assert.Equal("hi love", Assert.Single(fixture.LocalNotes.Notes, n => n.Id == "remote-m1").Text);
        // The note just revealed is the one being read.
        Assert.Equal("remote-m1", vm.SelectedOpenedNote?.Id);
        Assert.Equal("hi love", vm.SelectedOpenedNoteText);
    }

    [Fact]
    public async Task Reveal_puts_the_newest_note_first_and_refresh_keeps_that_order()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var first = fixture.AddPendingEnvelope("m1", "first");
        var second = fixture.AddPendingEnvelope("m2", "second");
        var vm = fixture.CreateLoveNotesViewModel();
        await vm.RefreshAsync(Ct);

        await vm.RevealRemoteNoteAsync(first, Ct);
        await vm.RevealRemoteNoteAsync(second, Ct);

        Assert.Equal(["remote-m2", "remote-m1"], vm.OpenedNotes.Select(n => n.Id).ToArray());
        Assert.Equal(["m1", "m2"], fixture.ConsumedMessageIds);

        await vm.RefreshAsync(Ct);

        Assert.Equal(["remote-m2", "remote-m1"], vm.OpenedNotes.Select(n => n.Id).ToArray());
        Assert.Empty(vm.PendingRemoteNotes);
        // The selection survives the reload on the fresh row.
        Assert.Same(vm.OpenedNotes[0], vm.SelectedOpenedNote);
    }

    [Fact]
    public async Task Opened_notes_list_only_partner_notes()
    {
        // Seeded defaults and older hand-written jar notes stay dormant in local_notes
        // and must never show up in the opened list.
        var fixture = SettingsDataPagesFixture.Create();
        fixture.LocalNotes.Notes.Add(new LocalLoveNote("default-1", "seeded note"));
        fixture.LocalNotes.Notes.Add(new LocalLoveNote("0f1e2d3c", "hand written jar note"));
        fixture.LocalNotes.Notes.Add(new LocalLoveNote("remote-old", "saved partner note"));
        var vm = fixture.CreateLoveNotesViewModel();

        await vm.RefreshAsync(Ct);

        Assert.Equal("remote-old", Assert.Single(vm.OpenedNotes).Id);
        Assert.False(vm.HasNoOpenedNotes);
    }

    [Fact]
    public async Task Deleting_an_opened_note_removes_it_from_the_list_and_the_repository()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var envelope = fixture.AddPendingEnvelope("m1", "hi love");
        var vm = fixture.CreateLoveNotesViewModel();
        await vm.RefreshAsync(Ct);
        await vm.RevealRemoteNoteAsync(envelope, Ct);
        var opened = Assert.Single(vm.OpenedNotes);

        vm.RequestDeleteOpenedNoteCommand.Execute(opened);
        Assert.True(vm.IsConfirmingDeleteNote);
        Assert.Single(fixture.LocalNotes.Notes);
        await vm.DeleteOpenedNoteCommand.ExecuteAsync(vm.PendingDeleteNote);

        Assert.Empty(vm.OpenedNotes);
        Assert.True(vm.HasNoOpenedNotes);
        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Null(vm.SelectedOpenedNote);
        Assert.False(vm.IsConfirmingDeleteNote);
        Assert.Equal("okkk note deleted le", vm.StatusMessage);
    }

    [Fact]
    public async Task A_failed_save_leaves_the_note_unopened_and_nothing_changes()
    {
        var fixture = SettingsDataPagesFixture.Create();
        var envelope = fixture.AddPendingEnvelope("m1", "hi love");
        var vm = fixture.CreateLoveNotesViewModel();
        await vm.RefreshAsync(Ct);
        fixture.Transactions.FailNextRemoteCommit = true;

        await vm.RevealRemoteNoteAsync(envelope, Ct);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(fixture.ConsumedMessageIds);
        Assert.Empty(fixture.LocalNotes.Notes);
        Assert.Empty(vm.OpenedNotes);
        Assert.Contains(vm.PendingRemoteNotes, e => e.MessageId == "m1");
        Assert.Empty(fixture.DiscardedHeldRemoteNoteIds);

        // It can simply be revealed again.
        await vm.RevealRemoteNoteAsync(vm.PendingRemoteNotes.Single(), Ct);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(["m1"], fixture.ConsumedMessageIds);
    }

    [Fact]
    public async Task Reveal_dismisses_the_unread_indicator_after_saving()
    {
        var events = new List<string>();
        SettingsDataPagesFixture? fixture = null;
        fixture = SettingsDataPagesFixture.Create(presentPetAsync: (petEvent, _) =>
        {
            // Record what was already committed when the pet was told the note is read.
            events.Add(petEvent is PetEvent.Dismissed dismissed
                ? $"dismiss:{dismissed.ItemId}:consumed={fixture!.ConsumedMessageIds.Count}:discarded={fixture.DiscardedHeldRemoteNoteIds.Count}"
                : "present");
            return Task.CompletedTask;
        });
        var envelope = fixture.AddPendingEnvelope("m1", "hi love");
        var vm = fixture.CreateLoveNotesViewModel();
        await vm.RefreshAsync(Ct);

        await vm.RevealRemoteNoteAsync(envelope, Ct);

        Assert.Equal(["dismiss:m1:consumed=1:discarded=1"], events);
    }
}
