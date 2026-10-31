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

    /// <summary>Toast confirming a Quick Clean triggered from the tray menu. Uses
    /// <see cref="QuickCleanService.SummaryText"/> so the notification repeats exactly
    /// what the Overview cards and the activity entry would say about the same run,
    /// including why a run that removed nothing came back empty.</summary>
    public static void ShowQuickCleanComplete(QuickCleanArea area, QuickCleanResult result)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText($"Quick Clean - {QuickCleanService.Title(area)}: {QuickCleanService.SummaryText(result)}")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch { /* notifications are best-effort */ }
    }

    /// <summary>Toast confirming the tray menu's "Clean Everything" run: one line
    /// with the combined total across all four areas (worded by MainWindow's
    /// <c>CleanEverythingSummary</c>, next to the pipeline that produced it), plus
    /// one line per failed area underneath - so a run with one dead area is honest
    /// about it without hiding what the other three reclaimed.</summary>
    public static void ShowCleanEverythingComplete(string summary, IReadOnlyList<string> failures)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText(summary);
            foreach (var failure in failures)
                builder.AddText(failure);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch { /* notifications are best-effort */ }
    }

    /// <summary>Toast telling the user a Quick Clean run failed. A run started from
    /// the tray is invisible while it works and its only failure record used to be
    /// the activity log, which the user may never open - so the tray-triggered paths
    /// show the failure right here as well as logging it.</summary>
    public static void ShowQuickCleanFailed(QuickCleanArea area, string message)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("CleanMachine")
                .AddText($"Quick Clean - {QuickCleanService.Title(area)} failed: {message}")
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
