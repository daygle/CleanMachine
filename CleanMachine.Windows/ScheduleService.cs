using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

/// <summary>Registers cleanup schedules with Windows Task Scheduler and runs them
/// headlessly when the scheduled task fires (even if the app is not open).</summary>
public sealed class ScheduleService
{
    /// <summary>The running executable, used as the scheduled task's program.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CleanMachine.exe");

    /// <summary>True when running inside an MSIX package (the exe path lives under
    /// WindowsApps and changes on every update).</summary>
    public static bool IsMsix { get; } = TryGetPackageFamilyName(out _);

    /// <summary>Returns the stable launch command for a scheduled task.
    /// For MSIX packages this activates the app by package identity via
    /// <c>explorer.exe shell:AppsFolder</c>, so the task never points at a
    /// versioned path that disappears after an update.
    /// For standalone installs the direct exe path is used.</summary>
    public static string GetLaunchCommand(string scheduleId)
    {
        if (IsMsix && TryGetPackageFamilyName(out var familyName))
        {
            // cmd.exe /c start with shell:AppsFolder activates the MSIX package
            // by identity - the path is stable across updates.
            return $"cmd.exe /c start \"\" \"shell:AppsFolder\\{familyName}!App\" --run-schedule {scheduleId}";
        }
        return $"\"{ExecutablePath}\" --run-schedule {scheduleId}";
    }

    public static async Task<bool> RegisterAsync(CleanupSchedule schedule, CancellationToken token = default)
    {
        var created = await RunProcessAsync("schtasks.exe",
            ScheduledTask.BuildCreateArguments(schedule, GetLaunchCommand(schedule.Id)), token);
        // "Wake the computer to run this task" isn't settable via schtasks.exe, so apply
        // it as a best-effort second step. Recreating the task with /F clears the flag,
        // so this only needs to run when wake is wanted. A failure (e.g. wake timers
        // disabled by policy) must not fail registration - the task still runs whenever
        // the PC is already awake.
        if (created && schedule.WakeToRun)
            await RunProcessAsync("powershell.exe", ScheduledTask.BuildWakeToRunArguments(schedule.Id), token);
        return created;
    }

    public static Task<bool> UnregisterAsync(string scheduleId, CancellationToken token = default)
        => RunProcessAsync("schtasks.exe", ScheduledTask.BuildDeleteArguments(scheduleId), token);

    /// <summary>Brings the machine's task store in line with the saved schedules:
    /// enabled schedules are (re)registered; disabled or empty ones are removed.</summary>
    public static async Task SyncAllAsync(AppSettings settings, CancellationToken token = default)
    {
        foreach (var schedule in settings.Schedules)
        {
            token.ThrowIfCancellationRequested();
            if (schedule.Enabled && ScheduledTask.HasWork(schedule))
                await RegisterAsync(schedule, token);
            else
                await UnregisterAsync(schedule.Id, token);
        }
    }

    /// <summary>Runs one schedule's cleanup. Failures in any one area are collected
    /// rather than thrown, so a partial run still records activity and never crashes
    /// the headless process.</summary>
    public static async Task<ScheduleRunResult> RunAsync(
        CleanupSchedule schedule, AppSettings settings, CancellationToken token = default)
    {
        var issues = new List<string>();
        var details = new List<string>();
        var items = 0;
        long bytes = 0;

        // Build secure delete options once; null when the schedule doesn't use it.
        SecureDeleteOptions? secureDelete = schedule.SecureDelete
            ? new SecureDeleteOptions(settings.SecureDeleteMethod, settings.CustomWipePasses)
            : null;

        try
        {
            var categories = WindowsCleanupService.Catalog
                .Where(c => schedule.WindowsCategoryIds.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (categories.Count > 0)
            {
                var report = await new WindowsCleanupService().CleanSelectedAsync(
                    categories,
                    new WindowsCleanupOptions(ConfirmReviewCategories: false, AllowElevation: false, ExcludedPaths: settings.ExcludedPaths, SecureDelete: schedule.SecureDelete, SecureDeleteOptions: secureDelete),
                    cancellationToken: token);
                items += report.Result.ItemsRemoved;
                bytes += report.Result.BytesRecovered;
                issues.AddRange(report.Skipped.Select(s => $"{s.Path}: {s.Reason}"));
                details.AddRange(ActivityStore.BreakdownLines(report.Breakdown) ?? []);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add($"Windows cleanup failed: {ex.Message}");
        }

        if (schedule.CleanBrowserCache)
        {
            try
            {
                var service = new BrowserCleanupService();
                var targets = await service.ScanAsync(settings.ProtectedBrowsers, excludedPaths: settings.ExcludedPaths, token: token);
                var report = await service.CleanWithReportAsync(
                    targets,
                    new BrowserCleanupOptions(settings.ExcludedPaths, RequireBrowsersClosed: false, SecureDelete: secureDelete),
                    token: token);
                items += report.Result.ItemsRemoved;
                bytes += report.Result.BytesRecovered;
                if (report.Result.ItemsRemoved > 0)
                    details.Add($"Browser caches - {report.Result.ItemsRemoved:N0} item(s), {AppNotifications.FormatBytes(report.Result.BytesRecovered)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Browser cleanup failed: {ex.Message}");
            }
        }

        if (schedule.CleanAppTempFiles)
        {
            try
            {
                var appService = new AppCleanupService();
                var scans = await appService.ScanAllAsync(token);
                // Reuse the page's "apps with items" rule: nothing to clean, nothing to do.
                var selection = scans
                    .Where(s => s.Installed && s.Items.Count > 0)
                    .SelectMany(s => s.Items.Select((_, index) => (s.Id, index)))
                    .ToList();
                if (selection.Count > 0)
                {
                    var report = await Task.Run(
                        () => appService.CleanAsync(selection, secureDelete, token), token);
                    items += report.Result.ItemsRemoved;
                    bytes += report.Result.BytesRecovered;
                    issues.AddRange(report.Skipped.Select(s => $"{s.Path}: {s.Reason}"));
                    if (report.Result.ItemsRemoved > 0)
                        details.Add($"Application temp files - {report.Result.ItemsRemoved:N0} item(s), {AppNotifications.FormatBytes(report.Result.BytesRecovered)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Application cleanup failed: {ex.Message}");
            }
        }

        if (schedule.RegistryCategories.Count > 0)
        {
            try
            {
                var service = new RegistryCareService();
                var scan = await service.ScanAsync(token);
                var selected = scan.Findings
                    .Where(f => schedule.RegistryCategories.Contains(f.Category, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                var review = await service.PrepareReviewAsync(selected, token);
                // Same rule as the manual page: never clean the registry without a backup.
                if (review.Findings.Count > 0 && review.Backups.Count == 0)
                    issues.Add("Registry cleanup skipped: no backup could be created.");
                else if (review.Findings.Count > 0)
                {
                    var registryRemoved = (await service.CleanAsync(review, token)).Removed;
                    items += registryRemoved;
                    if (registryRemoved > 0)
                        details.Add($"Registry - {registryRemoved:N0} item(s)");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add($"Registry cleanup failed: {ex.Message}");
            }
        }

        // Record with a fresh (uncancellable) token and await it, so a cancelled run
        // still persists its partial results before the headless process exits.
        await new CleanupStatsStore().RecordAsync(items, bytes, CancellationToken.None);
        await new ActivityStore().AddAsync(new ActivityEntry(
            DateTimeOffset.UtcNow,
            "Scheduled cleanup",
            $"'{schedule.Name}' cleaned {items:N0} item(s), {AppNotifications.FormatBytes(bytes)} recovered" +
            (issues.Count > 0 ? $" · {issues.Count} skipped" : string.Empty),
            details.Count > 0 ? details : null), token);

        if (schedule.AfterClean == ScheduleAction.Notify && items > 0)
            AppNotifications.ShowSystemCleanupComplete(new CleanupResult(items, bytes));

        ApplyPowerAction(schedule.AfterClean);
        return new ScheduleRunResult(items, bytes, issues);
    }

    /// <summary>Shutdown/restart use shutdown.exe with a 60-second grace period so the
    /// user can abort with <c>shutdown /a</c>; Sleep suspends immediately.</summary>
    private static void ApplyPowerAction(ScheduleAction action)
    {
        try
        {
            switch (action)
            {
                case ScheduleAction.Shutdown: StartShutdown("/s"); break;
                case ScheduleAction.Restart: StartShutdown("/r"); break;
                case ScheduleAction.Sleep: SetSuspendState(false, false, false); break;
            }
        }
        catch { /* a failed power action must never crash the headless run */ }
    }

    private static void StartShutdown(string flag) => Process.Start(new ProcessStartInfo(
        "shutdown.exe", $"{flag} /t 60 /c \"CleanMachine scheduled cleanup complete\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    });

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private static async Task<bool> RunProcessAsync(string fileName, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(psi);
        if (process is null) return false;
        await process.WaitForExitAsync(token);
        return process.ExitCode == 0;
    }

    private static bool TryGetPackageFamilyName(out string? familyName)
    {
        familyName = null;
        try
        {
            familyName = Package.Current.Id.FamilyName;
            return true;
        }
        catch
        {
            // Not running as an MSIX package - standalone .exe install.
            return false;
        }
    }
}
