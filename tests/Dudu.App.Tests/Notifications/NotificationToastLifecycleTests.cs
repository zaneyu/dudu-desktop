using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Notifications;

public sealed class NotificationToastLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reminder_toast_is_tagged_grouped_and_expires()
    {
        var sink = new RemovingSink();
        var service = new AppNotificationService(sink, () => Now);

        await service.ShowReminderAsync("reminder-1", "Stretch", TestContext.Current.CancellationToken);

        var request = Assert.Single(sink.Shown);
        Assert.Equal("reminder-1", request.Tag);
        Assert.Equal(AppNotificationService.ReminderGroup, request.Group);
        Assert.Equal(Now + AppNotificationService.ReminderToastLifetime, request.ExpirationTime);
    }

    [Fact]
    public async Task Dismissing_a_reminder_removes_the_toast_with_the_same_tag_and_group()
    {
        var sink = new RemovingSink();
        var service = new AppNotificationService(sink, () => Now);
        var longId = new string('a', 100);

        await service.ShowReminderAsync(longId, "Stretch", TestContext.Current.CancellationToken);
        await service.DismissReminderAsync(longId, TestContext.Current.CancellationToken);

        var shown = Assert.Single(sink.Shown);
        var removed = Assert.Single(sink.Removed);
        Assert.True(shown.Tag!.Length <= 64);
        Assert.Equal((shown.Tag, shown.Group), removed);
    }

    [Fact]
    public async Task Removal_failure_is_swallowed()
    {
        var sink = new RemovingSink { FailRemove = true };
        var service = new AppNotificationService(sink);

        await service.DismissReminderAsync("reminder-1", TestContext.Current.CancellationToken);

        Assert.True(service.NotificationsAvailable);
    }

    [Theory]
    [InlineData("a&action=open-note")]
    [InlineData("a;b=c")]
    [InlineData("100%")]
    public void Argument_values_are_escaped_and_round_trip_without_forging_pairs(string value)
    {
        var fragment = "action=reminder-done&" + NotificationArguments.Pair("reminderId", value);

        var pairs = NotificationArguments.Parse(fragment).ToArray();

        Assert.Equal([("action", "reminder-done"), ("reminderId", value)], pairs);
    }

    private sealed class RemovingSink : INotificationSink
    {
        public List<NotificationRequest> Shown { get; } = [];
        public List<(string Tag, string Group)> Removed { get; } = [];
        public bool FailRemove { get; init; }

        public Task<bool> TryRegisterAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
        {
            Shown.Add(request);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string tag, string group, CancellationToken cancellationToken)
        {
            if (FailRemove)
            {
                throw new InvalidOperationException("remove failed");
            }

            Removed.Add((tag, group));
            return Task.CompletedTask;
        }
    }
}
