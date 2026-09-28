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

    /// <summary>Runs the uninstall. Returns false only when the deferred removal
    /// helper could not be launched - in that case nothing has been touched, so
    /// the caller can point the user at Windows Settings > Apps instead.
    /// Every cleanup step is best-effort: a failed step only means that item is
    /// restored (or swept) on the next use, never that the uninstall stops.</summary>
    internal static bool Start(bool removeData)
    {
        if (!ScheduleService.IsMsix) return false;

        // Helper first: it must survive this process, and if it cannot start we
        // abort before changing anything on disk.
        if (!StartRemovalHelper()) return false;

        // 1. The self-heal desktop shortcut (not package-owned, never cleaned up
        //    by Windows on package removal).
        Try(() =>
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcut = Path.Combine(desktop, ShortcutFileName);
            if (File.Exists(shortcut)) File.Delete(shortcut);
        });

        // 2. Startup registration: the HKCU Run value written by StartupRegistration,
        //    together with its StartupApproved state (removed by the same call).
        Try(() => StartupRegistration.SetEnabled(false, string.Empty));

        // 3. Scheduled cleanup tasks live in the real Task Scheduler (folder
        //    \CleanMachine\), outside the package. PowerShell because schtasks
        //    cannot enumerate or delete by task folder.
        Try(() => Process.Start(PowerShellInfo(
            "-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask -TaskPath '\\CleanMachine\\' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false\"")));

        // 4. Scratch files under %TEMP%\CleanMachine. Older installs wrote there,
        //    and a registry backup that could not be written to the app's own
        //    folder falls back to it - see RegistryCareService.BackupDirectories.
        //    %TEMP% outlives the app, so sweep it either way.
        Try(() =>
        {
            var temp = Path.Combine(Path.GetTempPath(), FolderName);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
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
        else
        {
            // 5c. "Keep" must be honest: data still sitting package-local would be
            //     destroyed by the package removal, so copy it out to the durable
            //     folder first. This is a copy, never a move - the package removal
            //     is not ours to interrupt, and the app may still be running when
            //     the deferred helper gets to it.
            Try(() => AppDataPaths.CopyToDurableRoot());
        }

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
            Process.Start(PowerShellInfo(BuildRemovalCommand(fullName)));
            return true;
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
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    };

    private const string FolderName = AppDataPaths.FolderName;

    private static void Try(Action action)
    {
        try { action(); }
        catch { /* cleanup is best-effort; never block the uninstall */ }
    }
}
