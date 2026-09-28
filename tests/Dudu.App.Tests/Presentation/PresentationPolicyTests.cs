using Dudu.App.Presentation;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PresentationPolicyTests
{
    private const string NoteOne = "11111111-1111-4111-8111-111111111111";
    private const string NoteTwo = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void Quiet_hours_alone_hold_every_item_and_release_them_in_order_afterwards()
    {
        // No item kind ignores quiet hours any more (the bedtime routine that
        // did went with reminders), so quiet hours hold everything.
        var now = DateTimeOffset.Parse("2026-09-18T22:00:00Z");
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var first = DurableNotification.RemoteNote(NoteOne);
        var second = DurableNotification.RemoteNote(NoteTwo);
        policy.Enqueue(first);
        policy.Enqueue(second);

        var held = policy.Decide(nowQuiet: true, fullscreen: false, paused: false, nowUtc: now);
        Assert.Empty(held.ToPresent);
        Assert.Equal(2, held.RemainingQueuedCount);

        Assert.Equal(first, Assert.Single(policy.Decide(false, false, false, nowUtc: now).ToPresent));
        Assert.Equal(second, Assert.Single(policy.Decide(false, false, false, nowUtc: now).ToPresent));
    }

    [Fact]
    public void Leaving_quiet_hours_releases_one_durable_item_not_a_burst()
    {
        var policy = PresentationPolicyFixture.WithQueuedNotes(3);

        var decision = policy.Decide(nowQuiet: false, fullscreen: false, paused: false);

        Assert.Single(decision.ToPresent);
        Assert.Equal(2, decision.RemainingQueuedCount);
    }

    [Fact]
    public void Decide_does_not_release_while_still_suppressed()
    {
        var policy = PresentationPolicyFixture.WithQueuedNotes(1);

        var decision = policy.Decide(nowQuiet: true, fullscreen: false, paused: false);

        Assert.Empty(decision.ToPresent);
        Assert.Equal(1, decision.RemainingQueuedCount);
    }

    [Fact]
    public void Decide_withholds_a_second_release_within_the_minimum_silent_interval()
    {
        var policy = new PresentationPolicy(TimeSpan.FromSeconds(90));
        policy.Enqueue(DurableNotification.RemoteNote(NoteOne));
        policy.Enqueue(DurableNotification.RemoteNote(NoteTwo));
        var start = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        var first = policy.Decide(nowQuiet: false, fullscreen: false, paused: false, nowUtc: start);
        var second = policy.Decide(
            nowQuiet: false,
            fullscreen: false,
            paused: false,
            nowUtc: start.AddSeconds(30));
        var third = policy.Decide(
            nowQuiet: false,
            fullscreen: false,
            paused: false,
            nowUtc: start.AddSeconds(91));

        Assert.Single(first.ToPresent);
        Assert.Empty(second.ToPresent);
        Assert.Equal(1, second.RemainingQueuedCount);
        Assert.Single(third.ToPresent);
        Assert.Equal(0, third.RemainingQueuedCount);
    }

    [Fact]
    public void Enqueue_does_not_double_queue_the_same_note()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var first = policy.Enqueue(DurableNotification.RemoteNote(NoteOne));
        var second = policy.Enqueue(DurableNotification.RemoteNote(NoteOne));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, policy.QueuedCount);
    }

    [Fact]
    public void RemoteNote_rejects_a_non_guid_message_id_without_echoing_it()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DurableNotification.RemoteNote("super-secret-plaintext"));

        Assert.DoesNotContain("super-secret-plaintext", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsQueued_reflects_queue_membership_until_Decide_releases_the_item()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var item = DurableNotification.RemoteNote(NoteOne);
        policy.Enqueue(item);

        Assert.True(policy.IsQueued(item));

        var decision = policy.Decide(nowQuiet: false, fullscreen: false, paused: false);

        Assert.Single(decision.ToPresent);
        Assert.False(policy.IsQueued(item));
    }
}

file static class PresentationPolicyFixture
{
    public static PresentationPolicy WithQueuedNotes(int count)
    {
        var policy = new PresentationPolicy(TimeSpan.FromSeconds(90));
        for (var index = 0; index < count; index++)
        {
            policy.Enqueue(DurableNotification.RemoteNote(Guid.NewGuid().ToString("D")));
        }

        return policy;
    }
}
