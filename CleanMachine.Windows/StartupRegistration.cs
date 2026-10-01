using Microsoft.Win32;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

/// <summary>Starts CleanMachine at logon.
/// <para>
/// A packaged (MSIX) install uses the manifest's <c>windows.startupTask</c>
/// extension through <see cref="StartupTask"/>. A HKCU Run value does not work
/// there: the package's HKCU writes are redirected into its private registry
/// hive, so Windows never sees the value, and even a value that did land would
/// point into the version-stamped WindowsApps folder and launch the exe without
/// its package identity. The startup task is also what Task Manager and
/// Settings &gt; Apps &gt; Startup list for a Store app, so the user's own
/// on/off switch there is the one this follows.
/// </para>
/// <para>
/// An unpackaged build (a local debug run) falls back to the HKCU Run value,
/// rewritten whenever it goes stale: a value left pointing at a folder that no
/// longer exists stops the app auto-starting, and Registry Care flags the app's
/// own entry as a dead startup reference.
/// </para></summary>
public static class StartupRegistration
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "CleanMachine";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Switch = "--background";
    /// <summary>Must match the TaskId of the uap5:StartupTask in Package.appxmanifest.</summary>
    internal const string TaskId = "CleanMachineStartup";

    // Settings saves can fire in quick succession; serialising keeps an older
    // request from landing after a newer one.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Applies the user's choice after a settings change. Returns the
    /// packaged startup task's resulting state, or null for an unpackaged build
    /// or when the task could not be read.</summary>
    public static Task<StartupTaskState?> SetEnabledAsync(bool enabled, string? executablePath)
        => ApplyAsync(enabled, () => SetEnabled(enabled, executablePath ?? string.Empty));

    /// <summary>Launch-time counterpart of <see cref="SetEnabledAsync"/>: brings
    /// registration in line with the saved choice, repairing a stale Run value
    /// on an unpackaged build without rewriting a healthy one.</summary>
    public static Task<StartupTaskState?> SyncAsync(bool shouldRun, string? executablePath)
        => ApplyAsync(shouldRun, () => Sync(shouldRun, executablePath));

    /// <summary>The packaged startup task's current state, or null when there is
    /// none (unpackaged build) or it could not be read.</summary>
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        if (!ScheduleService.IsMsix) return null;
        try { return (await StartupTask.GetAsync(TaskId)).State; }
        catch { return null; }
    }

    /// <summary>True when the user (Task Manager, Settings &gt; Apps &gt; Startup)
    /// or a group policy has turned the startup task off. Windows does not let an
    /// app override either, so the UI has to point the user there instead.</summary>
    public static bool IsBlocked(StartupTaskState? state)
        => state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy;

    private static async Task<StartupTaskState?> ApplyAsync(bool enabled, Action applyRunValue)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ScheduleService.IsMsix)
            {
                applyRunValue();
                return null;
            }

            // Clear the Run value earlier versions wrote. It never started the
            // packaged app, and left in place it would be a second, broken
            // registration next to the startup task.
            try { RemoveCore(RunPath, ValueName); }
            catch { /* best-effort */ }

            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                // DisabledByUser/ByPolicy cannot be changed from here; only a
                // plain Disabled task can be turned on programmatically. A
                // full-trust app is enabled without a consent prompt.
                if (task.State == StartupTaskState.Disabled)
                    return await task.RequestEnableAsync();
            }
            else if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }
            return task.State;
        }
        catch
        {
            return null; // startup registration is best-effort
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled) Write(executablePath, RunPath, ValueName);
        else RemoveCore(RunPath, ValueName);
    }

    /// <summary>Brings the Run value in line with the user's choice, repairing a
    /// stale path but never overriding that choice: nothing is written when the
    /// user has startup off, and nothing is removed when they have it on.
    /// Returns true when the registry was actually changed.</summary>
    internal static bool Sync(bool shouldRun, string? executablePath)
        => SyncCore(shouldRun, executablePath, RunPath, ValueName);

    private static bool SyncCore(bool shouldRun, string? executablePath, string runPath, string valueName)
    {
        if (!shouldRun)
        {
            if (ReadCommand(runPath, valueName) is null) return false;
            RemoveCore(runPath, valueName);
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

        var current = ReadCommand(runPath, valueName);
        if (current is not null)
        {
            // Only rewrite our own value when it is unusable. A live path that
            // merely differs (a second install, say) is left alone rather than
            // fought over on every launch.
            var target = ResolveExecutable(current);
            if (target is not null && File.Exists(target)) return false;
        }

        Write(executablePath, runPath, valueName);
        return true;
    }

    public static bool IsEnabled() => ReadCommand(RunPath, ValueName) is not null;

    /// <summary>Test seam over <see cref="Sync"/>: the production path is hardcoded
    /// to the app's own value name, so tests need to redirect it at a scratch key
    /// rather than mutating the live Run entry on the machine running the suite.
    /// The repair decision itself - the part that can silently regress - is the
    /// same code either way.</summary>
    internal static bool SyncForTest(bool shouldRun, string? executablePath, string runPath, string valueName)
        => SyncCore(shouldRun, executablePath, runPath, valueName);

    private static void Write(string executablePath, string runPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(runPath);
        key.SetValue(valueName, $"\"{executablePath}\" {Switch}");
    }

    /// <summary>Deletes the Run value and the matching StartupApproved blob.
    /// Explorer keys its enabled/disabled state by the value name, so leaving
    /// the blob behind leaves stale state in Task Manager for an entry that no
    /// longer exists - and a later re-enable would inherit the old state.</summary>
    private static void RemoveCore(string runPath, string valueName)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(runPath, writable: true))
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true))
            approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static string? ReadCommand(string runPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath, writable: false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>The executable a stored Run command points at, or null when it
    /// cannot be resolved to a concrete local path (an env var, a bare name, or
    /// a Store activation). Uses the cleanup scanners' resolver so both agree on
    /// what counts as a real path.</summary>
    private static string? ResolveExecutable(string command)
        => CleanupService.ResolveStartupExecutable(command);
}
