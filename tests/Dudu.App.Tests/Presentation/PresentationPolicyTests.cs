using Dudu.App.Presentation;
using Xunit;

namespace Dudu.App.Tests.Presentation;

public sealed class PresentationPolicyTests
{
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
        policy.Enqueue(DurableNotification.Reminder("reminder-1", "Stretch"));
        policy.Enqueue(DurableNotification.Reminder("reminder-2", "Hydrate"));
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
    public void Enqueue_does_not_double_queue_the_same_reminder()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);
        var first = policy.Enqueue(DurableNotification.Reminder("reminder-1", "Stretch"));
        var second = policy.Enqueue(DurableNotification.Reminder("reminder-1", "Stretch"));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, policy.QueuedCount);
    }

    [Fact]
    public void Enqueue_discards_ambient_items_instead_of_queueing_them()
    {
        var policy = new PresentationPolicy(TimeSpan.Zero);

        var enqueued = policy.Enqueue(DurableNotification.Ambient("idle"));

        Assert.False(enqueued);
        Assert.Equal(0, policy.QueuedCount);
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
        var item = DurableNotification.Reminder("reminder-1", "Stretch");
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
