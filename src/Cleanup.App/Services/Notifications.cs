using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Cleanup.App.Services;

/// <summary>
/// Windows app notifications (AppNotificationManager). Windows asks no permission; the user controls them in
/// Configurações › Notificações, and Cleanup has its own switch in Ajustes. Clicking one follows a cleanup: link
/// (navigate or read-only scan only).
/// </summary>
public static class Notifications
{
    private static bool registered;

    public static void Register(Action<string?> onInvoked)
    {
        if (registered) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
                onInvoked(args.Arguments.TryGetValue("link", out var link) ? link : null);
            AppNotificationManager.Default.Register();
            registered = true;
        }
        catch (Exception e) { Log.Write("notifications: " + e.Message); }
    }

    public static void Unregister()
    {
        if (!registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception e) { Log.Write("notifications: " + e.Message); }
        registered = false;
    }

    /// <summary>Whether Windows lets Cleanup show notifications (Configurações › Notificações).</summary>
    public static bool IsAllowedBySystem
    {
        get
        {
            try { return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled; }
            catch (Exception) { return true; }
        }
    }

    public static void Show(string title, string body, string link)
    {
        if (!registered) return;
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("link", link)
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e) { Log.Write("notify: " + e.Message); }
    }
}
