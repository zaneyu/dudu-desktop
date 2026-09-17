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
    // Must stay in sync with Package.appxmanifest's toast activator and COM
    // server class. Packaged registration requires that server to point at
    // this same executable; unpackaged registration remains automatic.
    public const string PackagedActivatorClsid = "41CED5B9-5F7D-46C9-BA84-90D8B16BAC84";

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

        var notification = builder.BuildNotification();
        if (request.Tag is not null)
        {
            notification.Tag = request.Tag;
        }

        if (request.Group is not null)
        {
            notification.Group = request.Group;
        }

        if (request.ExpirationTime is { } expiration)
        {
            notification.Expiration = expiration;
        }

        AppNotificationManager.Default.Show(notification);
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(string tag, string group, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentException.ThrowIfNullOrEmpty(group);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group)
            .AsTask(cancellationToken);
    }
}
