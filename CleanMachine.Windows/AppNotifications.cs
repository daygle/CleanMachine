using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CleanMachine.Windows;

public static class AppNotifications
{
    public static void Register()
    {
        try { AppNotificationManager.Default.Register(); }
        catch { /* notifications are best-effort (e.g. elevated apps are unsupported) */ }
    }

    public static void ShowCleanupComplete(CleanupResult result)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText($"Browser cleanup complete: {result.ItemsRemoved:N0} items removed, {FormatBytes(result.BytesRecovered)} recovered.")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch { /* notifications are best-effort */ }
    }

    public static void ShowSystemCleanupComplete(CleanupResult result)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText($"System cleanup complete: {result.ItemsRemoved:N0} items removed, {FormatBytes(result.BytesRecovered)} recovered.")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch { /* notifications are best-effort */ }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }
}
