using Dudu.App.Hosting;
using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Notifications;

/// <summary>
/// A toast click that launches Dudu (cold start) is handled by App once the
/// runtime is composed, through the same <see cref="NotificationInvocationRouter"/>
/// used while running. Reminders were removed, so a reminder toast an older
/// build left in Action Center must do nothing at all when clicked -- cold or
/// running: no action, no navigation (there is no Reminders page), no throw.
/// </summary>
public sealed class NotificationColdStartTests
{
    public static TheoryData<string> LegacyReminderArguments =>
    [
        "action=reminder-done&reminderId=r-1",
        "action=reminder-snooze&reminderId=r-1",
        "action=open-reminder&reminderId=r-1",
        "action=reminder-done;reminderId=default-hydration",
        "action=reminder-snooze;reminderId=default-break",
        "action=open-reminder;reminderId=default-hydration",
    ];

    [Theory]
    [MemberData(nameof(LegacyReminderArguments))]
    public async Task A_cold_start_legacy_reminder_click_does_nothing(string arguments)
    {
        var navigated = new List<string>();
        var reporter = new RecordingReporter();
        var router = new NotificationInvocationRouter(
            (destination, _) => { navigated.Add(destination); return Task.CompletedTask; },
            reporter);

        // Exactly what App does on a cold start: parse the launching
        // activation, then hand it to the composed router.
        var activation = NotificationActivation.TryParse(arguments);
        await router.HandleActivationAsync(activation, TestContext.Current.CancellationToken);

        Assert.Null(activation);
        Assert.Empty(navigated);
        Assert.Empty(reporter.Operations);
    }

    [Theory]
    [InlineData("reminder-done")]
    [InlineData("reminder-snooze")]
    [InlineData("open-reminder")]
    public async Task A_running_legacy_reminder_click_does_nothing(string action)
    {
        var navigated = new List<string>();
        var reporter = new RecordingReporter();
        var router = new NotificationInvocationRouter(
            (destination, _) => { navigated.Add(destination); return Task.CompletedTask; },
            reporter);

        // The overload the composition's NotificationInvoked handler calls
        // with the SDK's own parsed argument map.
        await router.HandleAsync(
            new Dictionary<string, string>
            {
                ["action"] = action,
                ["reminderId"] = "default-hydration",
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(navigated);
        Assert.Empty(reporter.Operations);
    }

    [Fact]
    public async Task A_note_toast_click_opens_love_notes()
    {
        var navigated = new List<string>();
        var router = new NotificationInvocationRouter(
            (destination, _) => { navigated.Add(destination); return Task.CompletedTask; });

        await router.HandleActivationAsync(
            NotificationActivation.TryParse("action=open-note&messageId=11111111-1111-4111-8111-111111111111"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["notes"], navigated);
    }

    [Fact]
    public async Task A_navigation_failure_is_reported_not_thrown()
    {
        var reporter = new RecordingReporter();
        var router = new NotificationInvocationRouter(
            (_, _) => throw new InvalidOperationException("dispatcher gone"),
            reporter);

        await router.HandleActivationAsync(
            NotificationActivation.TryParse("action=open-note&messageId=11111111-1111-4111-8111-111111111111"),
            TestContext.Current.CancellationToken);

        Assert.Equal([NotificationInvocationRouter.NavigateOperation], reporter.Operations);
    }

    [Fact]
    public void App_routes_the_launching_activation_through_the_composed_router()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Dudu.App", "App.xaml.cs"));
        var composition = File.ReadAllText(Path.Combine(
            root, "src", "Dudu.App", "Hosting", "WindowsCompanionProductionComposition.cs"));

        Assert.Contains("if (notificationActivation is not null)", app, StringComparison.Ordinal);
        Assert.Contains("notificationRouter.HandleActivationAsync(notificationActivation", app, StringComparison.Ordinal);
        Assert.Contains("activation?.Destination", app, StringComparison.Ordinal);
        Assert.Contains("NotificationRouter = composedNotificationRouter", composition, StringComparison.Ordinal);
        // No reminder toast path survives anywhere in the activation wiring.
        Assert.DoesNotContain("ActsInBackground", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ReminderToastActions", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("ReminderActions", composition, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }

    private sealed class RecordingReporter : IAppHostErrorReporter
    {
        public List<string> Operations { get; } = [];

        public void Report(string operation, Exception exception) => Operations.Add(operation);
    }
}
