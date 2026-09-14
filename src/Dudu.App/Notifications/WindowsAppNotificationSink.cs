using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Dudu.App.Notifications;

/// <summary>
/// The real Windows App SDK notification surface for an unpackaged app.
/// Every failure here is the caller's (<see cref="AppNotificationService"/>)
/// responsibility to catch; this type never swallows exceptions itself so the
/// caller can flip its availability flag accurately.
/// </summary>
public sealed class WindowsAppNotificationSink : INotificationSink
{
    public Task<bool> TryRegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        AppNotificationManager.Default.Register();
        return Task.FromResult(true);
    }

    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        var builder = new AppNotificationBuilder().AddText(request.Title);
        if (request.Body is not null)
        {
            builder.AddText(request.Body);
        }

        foreach (var (key, value) in NotificationArguments.Parse(request.ActivationArguments))
        {
            builder.AddArgument(key, value);
        }

        foreach (var button in request.Buttons)
        {
            var appButton = new AppNotificationButton(button.Label);
            foreach (var (key, value) in NotificationArguments.Parse(button.ActivationArguments))
            {
                appButton.AddArgument(key, value);
            }

            builder.AddButton(appButton);
        }

        AppNotificationManager.Default.Show(builder.BuildNotification());
        return Task.CompletedTask;
    }
}
