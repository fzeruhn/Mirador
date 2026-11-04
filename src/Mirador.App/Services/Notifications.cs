using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Mirador.App.Services;

/// <summary>Windows notifications (toasts), for what the user must hear about while the window is hidden.</summary>
public static class Notifications
{
    private static bool registered;

    /// <summary>
    /// Registers Mirador with Windows' notifications (once, at start; Mirador is unpackaged). Clicking a notification
    /// calls <paramref name="clicked"/> on a background thread.
    /// </summary>
    public static void Register(Action clicked)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, _) => clicked();
            manager.Register();
            registered = true;
        }
        catch (Exception e)
        {
            // Without them Mirador still works: failures show in the window and the tray icon.
            AppLog.Write("Windows notifications aren't available", e);
        }
    }

    /// <summary>Shows a notification with a title and a line of text; nothing when notifications aren't available.</summary>
    public static void Show(string title, string text)
    {
        if (!registered)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            AppLog.Write("A notification couldn't be shown", e);
        }
    }

    public static void Unregister()
    {
        if (!registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception e)
        {
            AppLog.Write("Windows notifications couldn't be unregistered", e);
        }

        registered = false;
    }
}
