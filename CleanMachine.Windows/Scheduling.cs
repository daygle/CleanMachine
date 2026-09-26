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

    /// <summary>When true, Windows wakes the computer from sleep to run this task
    /// (timed triggers only). Depends on the machine's power settings allowing wake
    /// timers; Windows ignores it otherwise.</summary>
    public bool WakeToRun { get; set; }

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

    public static string TaskName(string scheduleId)
    {
        ValidateScheduleId(scheduleId);
        return $@"{Folder}\Cleanup-{scheduleId}";
    }

    /// <summary>Builds schtasks /Create arguments using the full launch command
    /// (e.g. the exe path or a shell:AppsFolder identity for MSIX).</summary>
    public static string BuildCreateArguments(CleanupSchedule schedule, string launchCommand)
    {
        // The launch command already contains its own quotes (around the exe path, or
        // the shell:AppsFolder identity for MSIX). schtasks wraps /TR in quotes, so the
        // inner quotes must be escaped as \" - otherwise the doubled quotes make schtasks
        // reject the task ("could not register"), especially when the path has spaces
        // (e.g. C:\Program Files\...).
        var action = $"/TR \"{launchCommand.Replace("\"", "\\\"")}\"";
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

    /// <summary>Builds powershell.exe arguments that enable "Wake the computer to run
    /// this task" on an already-created task. schtasks.exe cannot set this flag, so it
    /// is applied as a best-effort second step. Only the WakeToRun setting is changed;
    /// the task's other settings, triggers, and principal are preserved.</summary>
    public static string BuildWakeToRunArguments(string scheduleId)
    {
        ValidateScheduleId(scheduleId);
        var name = $"Cleanup-{scheduleId}";
        var script =
            "$ErrorActionPreference='Stop';" +
            $"$t=Get-ScheduledTask -TaskPath '\\{Folder}\\' -TaskName '{name}';" +
            "$t.Settings.WakeToRun=$true;" +
            $"Set-ScheduledTask -TaskPath '\\{Folder}\\' -TaskName '{name}' -Settings $t.Settings | Out-Null";
        return $"-NoProfile -NonInteractive -Command \"{script}\"";
    }

    /// <summary>True when the schedule actually has something to clean.</summary>
    public static bool HasWork(CleanupSchedule schedule)
        => schedule.WindowsCategoryIds.Count > 0 || schedule.CleanBrowserCache
           || schedule.CleanAppTempFiles || schedule.RegistryCategories.Count > 0;

    private static void ValidateScheduleId(string scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId)
            || scheduleId.Length > 64
            || scheduleId.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Schedule id contains unsupported characters.", nameof(scheduleId));
    }

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
