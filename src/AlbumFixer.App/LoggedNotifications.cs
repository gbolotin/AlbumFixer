using Microsoft.Extensions.Logging;
using WpfFoundation.Notifications;

namespace AlbumFixer.App;

/// <summary>
/// Passes OperationFeedback's notifications to Windows, logging a failure instead of throwing it into the run that
/// just finished. Notifications are a convenience: the taskbar and the window still report every result.
/// </summary>
public sealed class LoggedNotifications(WindowsNotificationService notifications, ILogger<LoggedNotifications> logger) : INotificationService
{
    public void Register(string appId, string displayName, string? iconPath) => notifications.Register(appId, displayName, iconPath);

    public void Unregister() => notifications.Unregister();

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Information)
    {
        try
        {
            notifications.Show(title, message, kind);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not show the notification for {Operation}", title);
        }
    }
}
