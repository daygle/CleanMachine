using System.Diagnostics;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

/// <summary>Uninstalls the MSIX package from inside the app. Windows offers no
/// uninstall hook for MSIX: Settings > Uninstall just removes the package, which
/// left the self-heal desktop shortcut behind and silently destroyed the
/// package-local data without ever asking. So the app does it itself - remove
/// everything the package does not own (desktop shortcut, scheduled cleanup
/// tasks, startup entry),
/// optionally delete the data folder after the user was asked, and remove the
/// package itself via a deferred helper, because Remove-AppxPackage refuses to
/// run while the package's own processes are alive.</summary>
internal static class MsixUninstallService
{
    /// <summary>The self-heal shortcut the app writes on launch (see
    /// MainWindow.CreateDesktopShortcut); plain file, invisible to Windows'
    /// package removal.</summary>
    internal const string ShortcutFileName = "CleanMachine.lnk";

    /// <summary>The target a desktop shortcut must use for an MSIX install: the
    /// package identity, not the executable. A shortcut aimed at
    /// C:\Program Files\WindowsApps\daygle.CleanMachine_1.0.0.0_x64__&lt;hash&gt;\CleanMachine.exe
    /// breaks on the very next update, because that version-stamped folder is deleted
    /// when the package is replaced. shell:AppsFolder\&lt;PFN&gt;!App is resolved by the
    /// shell on every launch and therefore survives every future update.</summary>
    internal static string BuildMsixShortcutTarget(string familyName) => $"shell:AppsFolder\\{familyName}!App";

    /// <summary>Starts the uninstall. A keep-data request is refused before any
    /// external cleanup or package-removal helper starts unless every eligible
    /// legacy file has been copied to the durable root and verified. Best-effort
    /// external cleanup failures are reported to the caller where they affect the
    /// user's requested data outcome.</summary>
    internal static bool Start(bool removeData, out string? error)
    {
        error = null;
        if (!ScheduleService.IsMsix)
        {
            error = "This uninstall flow is only available for the packaged app.";
            return false;
        }

        // Keeping data is a hard precondition, not a best-effort side effect. The
        // package folder is removed by Windows, so do not launch its remover until
        // migration reports complete.
        if (!removeData)
        {
            var migration = AppDataPaths.CopyToDurableRootResult();
            if (!migration.Complete)
            {
                error = migration.Error ?? "CleanMachine could not verify that all data was copied. Nothing was removed; your original data remains in place.";
                return false;
            }
        }

        // Helper first: it must survive this process, and if it cannot start we
        // abort before changing shortcuts, startup settings, or scheduled tasks.
        if (!StartRemovalHelper())
        {
            error = "Could not start the Windows package-removal helper. Nothing else was changed.";
            return false;
        }

        // 1. The self-heal desktop shortcut (not package-owned, never cleaned up
        //    by Windows on package removal).
        Try(() =>
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcut = Path.Combine(desktop, ShortcutFileName);
            if (File.Exists(shortcut)) File.Delete(shortcut);
        });

        // 2. Startup registration: the HKCU Run value older versions wrote, together
        //    with its StartupApproved state (removed by the same call). The current
        //    packaged startup task is part of the package and goes with it.
        Try(() => StartupRegistration.SetEnabled(false, string.Empty));

        // 3. Scheduled cleanup tasks live in the real Task Scheduler (folder
        //    \CleanMachine\), outside the package. PowerShell because schtasks
        //    cannot enumerate or delete by task folder.
        Try(() => Process.Start(PowerShellInfo(
            "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask -TaskPath '\\CleanMachine\\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false\"")));

        // 4. Scratch files under %TEMP%\CleanMachine. Older installs wrote there,
        //    and a registry backup that could not be written to the app's own
        //    folder falls back to it - see RegistryCareService.BackupDirectories.
        //    These can be the only copies of restore points, so "Keep data"
        //    must not sweep this fallback folder. Backups can still be discovered
        //    there by a future reinstall.
        if (removeData) Try(() =>
        {
            var temp = Path.Combine(TrustedTemp.Root, FolderName);
            if (Directory.Exists(temp) && !NativeSafety.IsReparsePoint(temp))
                Directory.Delete(temp, recursive: true);
        });

        if (removeData)
        {
            // 5a. The canonical data folder (user opted into deleting it).
            Try(DeleteData(AppDataPaths.Root));
            // 5b. Plus any legacy copies. The package-local one would die with the
            //     package anyway; deleting it now makes the choice honest for a
            //     reinstall. The %LOCALAPPDATA% one can hold data written by
            //     builds that predate the durable folder.
            Try(DeleteData(AppDataPaths.PackageLocalRoot));
            Try(DeleteData(AppDataPaths.RealRoot));
        }
        // Keep-data was copied and verified before any cleanup/helper launch.

        return true;
    }

    /// <summary>Builds the exact command the deferred helper runs: wait for this
    /// process to exit, then remove the package by full name, retrying a few
    /// times in case shutdown takes longer than the first sleep. Exposed for
    /// tests because it embeds the package identity - quoting must be airtight.</summary>
    internal static string BuildRemovalCommand(string packageFullName)
    {
        // Single quotes are the only PowerShell string delimiter used here; double
        // them so an identity containing one cannot escape the literal.
        var escaped = packageFullName.Replace("'", "''");
        return "-NoProfile -ExecutionPolicy Bypass -Command \""
            + "$fn = '" + escaped + "'; "
            + "for ($i = 0; $i -lt 4; $i++) { "
            + "$p = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $fn }; "
            + "if (-not $p) { break }; "
            + "try { $p | Remove-AppxPackage -ErrorAction Stop; break } catch { Start-Sleep -Seconds 5 } }\"";
    }

    private static bool StartRemovalHelper()
    {
        try
        {
            var fullName = Package.Current.Id.FullName;
            using var helper = Process.Start(PowerShellInfo(BuildRemovalCommand(fullName)));
            return helper is not null;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo PowerShellInfo(string arguments) => new()
    {
        FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"),
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static Action DeleteData(string path) => () =>
    {
        if (Directory.Exists(path) && !NativeSafety.IsReparsePoint(path)) Directory.Delete(path, recursive: true);
    };

    private const string FolderName = AppDataPaths.FolderName;

    private static void Try(Action action)
    {
        try { action(); }
        catch { /* cleanup is best-effort; never block the uninstall */ }
    }
}
