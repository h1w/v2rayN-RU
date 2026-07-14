using Avalonia.Controls.Notifications;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Manager;

public class ToastService
{
    private const string ConnectedTitle = "Подключено";

    private readonly WindowNotificationManager? _manager;

    private string? _lastConnectedSummary;
    private DateTime _lastConnectedAt;

    public ToastService(TopLevel? topLevel)
    {
        if (topLevel != null)
        {
            _manager = new WindowNotificationManager(topLevel)
            {
                MaxItems = 3,
                Position = NotificationPosition.TopRight
            };
        }
    }

    public void Show(string? message, ToastType type)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, message, ToNotificationType(type)));
    }

    public void ShowConnected(string? serverSummary)
    {
        if (string.IsNullOrWhiteSpace(serverSummary))
        {
            return;
        }

        // Dedup: the same connect can arrive both from the profile-switch action and from the
        // core's connection summary; suppress a repeat for the same server within a short window.
        var now = DateTime.Now;
        if (serverSummary == _lastConnectedSummary && (now - _lastConnectedAt).TotalSeconds < 3)
        {
            return;
        }
        _lastConnectedSummary = serverSummary;
        _lastConnectedAt = now;

        _manager?.Show(new Avalonia.Controls.Notifications.Notification(ConnectedTitle, serverSummary, NotificationType.Success));
    }

    public void ShowUpdateAvailable()
    {
        _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, ResUI.menuNewUpdate, NotificationType.Information));
    }

    private static NotificationType ToNotificationType(ToastType type) => type switch
    {
        ToastType.Success => NotificationType.Success,
        ToastType.Warning => NotificationType.Warning,
        ToastType.Error => NotificationType.Error,
        _ => NotificationType.Information
    };
}
