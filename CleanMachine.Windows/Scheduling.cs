namespace CleanMachine.Windows;

/// <summary>When a cleanup schedule fires. "AtLogon" is the practical, admin-free
/// equivalent of "on PC startup" for a per-user app - a true machine-startup task
/// runs without a user session and cannot touch per-user data.</summary>
public enum ScheduleTrigger
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    AtLogon = 3
}

/// <summary>What happens after a scheduled cleanup completes successfully.</summary>
public enum ScheduleAction
{
    Nothing = 0,
    Notify = 1,
    Shutdown = 2,
    Restart = 3,
    Sleep = 4
}

/// <summary>A user-defined cleanup that runs on a Windows Scheduled Task, even when
/// the app is closed. The task launches this executable with
/// <c>--run-schedule &lt;id&gt;</c>; the app then runs headless.</summary>
public sealed class CleanupSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scheduled cleanup";
    public bool Enabled { get; set; } = true;
    public ScheduleTrigger Trigger { get; set; } = ScheduleTrigger.Weekly;
    public int Hour { get; set; } = 3;
    public int Minute { get; set; }
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Windows cleanup category ids (see <see cref="WindowsCleanupService.Catalog"/>).</summary>
    public List<string> WindowsCategoryIds { get; set; } = [];

    /// <summary>Clean every monitored browser's cache.</summary>
    public bool CleanBrowserCache { get; set; }

    /// <summary>Clean temp files for every detected application (Application Cleanup).</summary>
    public bool CleanAppTempFiles { get; set; }

    /// <summary>Registry Care category names (e.g. "MUI Cache", "Windows Startup").</summary>
    public List<string> RegistryCategories { get; set; } = [];

    public ScheduleAction AfterClean { get; set; } = ScheduleAction.Nothing;

    /// <summary>When true, files are overwritten before deletion using the
    /// method from AppSettings.SecureDeleteMethod.</summary>
    public bool SecureDelete { get; set; }

    /// <summary>A short human-readable trigger summary for lists.</summary>
    public string TriggerSummary() => Trigger switch
    {
        ScheduleTrigger.Daily => $"Daily at {Hour:00}:{Minute:00}",
        ScheduleTrigger.Weekly => $"Weekly on {DayOfWeek} at {Hour:00}:{Minute:00}",
        ScheduleTrigger.Monthly => $"Monthly on day {DayOfMonth} at {Hour:00}:{Minute:00}",
        _ => "At logon"
    };
}

/// <summary>Builds Windows Scheduled Task (schtasks.exe) command lines. Kept pure so
/// the argument shape can be unit-tested without touching the machine's task store.</summary>
public static class ScheduledTask
{
    /// <summary>All CleanMachine tasks live under this folder, so they are easy to
    /// find (and clean up) in Task Scheduler.</summary>
    public const string Folder = "CleanMachine";

    public static string TaskName(string scheduleId) => $@"{Folder}\Cleanup-{scheduleId}";

    /// <summary>Builds schtasks /Create arguments using the full launch command
    /// (e.g. the exe path or a shell:AppsFolder identity for MSIX).</summary>
    public static string BuildCreateArguments(CleanupSchedule schedule, string launchCommand)
    {
        var action = $"/TR \"{launchCommand}\"";
        var trigger = schedule.Trigger switch
        {
            ScheduleTrigger.Daily => $"/SC DAILY /ST {Clock(schedule)}",
            ScheduleTrigger.Weekly => $"/SC WEEKLY /D {DayToken(schedule.DayOfWeek)} /ST {Clock(schedule)}",
            ScheduleTrigger.Monthly => $"/SC MONTHLY /D {Math.Clamp(schedule.DayOfMonth, 1, 31)} /ST {Clock(schedule)}",
            _ => "/SC ONLOGON"
        };
        // /RL LIMITED: least privilege, so creating and running the task never needs admin.
        // /F: overwrite any existing task with the same name.
        return $"/Create /TN \"{TaskName(schedule.Id)}\" {action} {trigger} /RL LIMITED /F";
    }

    public static string BuildDeleteArguments(string scheduleId)
        => $"/Delete /TN \"{TaskName(scheduleId)}\" /F";

    /// <summary>True when the schedule actually has something to clean.</summary>
    public static bool HasWork(CleanupSchedule schedule)
        => schedule.WindowsCategoryIds.Count > 0 || schedule.CleanBrowserCache
           || schedule.CleanAppTempFiles || schedule.RegistryCategories.Count > 0;

    private static string Clock(CleanupSchedule schedule)
        => $"{Math.Clamp(schedule.Hour, 0, 23):00}:{Math.Clamp(schedule.Minute, 0, 59):00}";

    private static string DayToken(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN"
    };
}

public sealed record ScheduleRunResult(int ItemsRemoved, long BytesRecovered, IReadOnlyList<string> Issues);
