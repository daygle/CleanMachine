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

    /// <summary>Toast for a manual or browser-triggered run, e.g. "Browser cleanup
    /// complete: ...". Shares the <see cref="ShowAutomaticCleanupComplete(string, long, long)"/>
    /// body so every toast stays best-effort and identically formatted.</summary>
    public static void ShowCleanupComplete(string what, CleanupResult result)
        => ShowAutomaticCleanupComplete(what, result);

    public static void ShowSystemCleanupComplete(CleanupResult result)
        => ShowCleanupComplete("System cleanup complete", result);

    /// <summary>Toast for the opt-in notify option on the Automatic Cleanup page's
    /// startup and idle triggers (browser-exit and low-disk have their own toasts).</summary>
    public static void ShowAutomaticCleanupComplete(string trigger, CleanupResult result)
        => ShowAutomaticCleanupComplete(trigger, result.ItemsRemoved, result.BytesRecovered);

    public static void ShowAutomaticCleanupComplete(string trigger, long itemsRemoved, long bytesRecovered)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText($"{trigger}: {itemsRemoved:N0} items removed, {FormatBytes(bytesRecovered)} recovered.")
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
