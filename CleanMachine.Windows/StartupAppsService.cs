using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

/// <summary>Describes a single auto-start entry found in the registry or startup folders.</summary>
public sealed record StartupEntry(
    string Name,
    string Command,
    string? ExecutablePath,
    StartupSource Source,
    bool Enabled,
    string Section,
    bool IsOrphan = false,
    string RegistryPath = "",
    string ValueName = "",
    string? FilePath = null);

public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    StartupFolder
}

/// <summary>Enumerates and manages Windows startup entries from the Run and RunOnce
/// keys and the per-user and all-users Startup folders. Enable/disable uses the
/// Explorer StartupApproved convention (the same one Task Manager uses), so changes
/// made here are visible in Task Manager and vice versa. Disabling a startup-folder
/// file also uses the StartupApproved\StartupFolder key rather than renaming, so the
/// file itself is never touched.</summary>
public sealed class StartupAppsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ExplorerRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ExplorerRunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\RunOnce";
    // 32-bit programs' all-users Run entries live in the WOW64 view of HKLM, and
    // Explorer/Task Manager keep their enabled state under a separate "Run32" key.
    private const string Wow64RunKey = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ExplorerRun32Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ExplorerStartupFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>Binary value Explorer writes for a disabled entry (first byte odd).</summary>
    private static readonly byte[] DisabledApproval = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        ScanRegistry(RegistryHive.CurrentUser, StartupSource.RegistryCurrentUser, "Current User", entries);
        ScanRegistry(RegistryHive.LocalMachine, StartupSource.RegistryLocalMachine, "All Users", entries);
        ScanWow64Run(entries);
        ScanStartupFolder(StartupSource.StartupFolder, "Startup Folder", entries);
        return entries;
    }

    private static void ScanRegistry(RegistryHive hive, StartupSource source, string section, List<StartupEntry> entries)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var (keyPath, approvalPath) in new[] { (RunKey, ExplorerRunKey), (RunOnceKey, ExplorerRunOnceKey) })
            {
                using var runKey = root.OpenSubKey(keyPath);
                if (runKey is null) continue;

                foreach (var valueName in runKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valueName)) continue;
                    try
                    {
                        if (runKey.GetValueKind(valueName) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                    }
                    catch { continue; }

                    var command = runKey.GetValue(valueName) as string ?? "";
                    if (string.IsNullOrWhiteSpace(command)) continue;
                    var exePath = ResolveExecutable(command);
                    var enabled = IsRegistryEntryEnabled(root, approvalPath, valueName);
                    var isOrphan = exePath is not null && CleanupService.IsMissingFile(exePath);
                    entries.Add(new StartupEntry(
                        FriendlyName(valueName),
                        command,
                        exePath,
                        source,
                        enabled,
                        section,
                        isOrphan,
                        RegistryPath: $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{keyPath}",
                        ValueName: valueName));
                }
            }
        }
        catch { /* inaccessible hive - skip silently */ }
    }

    /// <summary>All-users Run entries registered by 32-bit programs (the WOW64
    /// view of HKLM). Task Manager lists these; reading only the 64-bit Run key
    /// hid them from this page entirely.</summary>
    private static void ScanWow64Run(List<StartupEntry> entries)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var runKey = root.OpenSubKey(Wow64RunKey);
            if (runKey is null) return;
            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                try
                {
                    if (runKey.GetValueKind(valueName) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                }
                catch { continue; }
                var command = runKey.GetValue(valueName) as string ?? "";
                if (string.IsNullOrWhiteSpace(command)) continue;
                var exePath = ResolveExecutable(command);
                entries.Add(new StartupEntry(
                    FriendlyName(valueName),
                    command,
                    exePath,
                    StartupSource.RegistryLocalMachine,
                    IsRegistryEntryEnabled(root, ExplorerRun32Key, valueName),
                    "All Users",
                    exePath is not null && CleanupService.IsMissingFile(exePath),
                    RegistryPath: $"HKLM\\{Wow64RunKey}",
                    ValueName: valueName));
            }
        }
        catch { /* inaccessible hive - skip silently */ }
    }

    /// <summary>The StartupApproved key that holds an entry's enabled state.</summary>
    private static string ApprovalPathFor(StartupEntry entry)
        => entry.RegistryPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? ExplorerRun32Key
            : entry.RegistryPath.EndsWith("RunOnce", StringComparison.Ordinal) ? ExplorerRunOnceKey
            : ExplorerRunKey;

    private static bool IsRegistryEntryEnabled(RegistryKey root, string approvalPath, string valueName)
    {
        // The StartupApproved key stores a binary value where the first byte
        // determines enabled (even) vs disabled (odd).
        try
        {
            using var approved = root.OpenSubKey(approvalPath);
            if (approved?.GetValue(valueName) is byte[] data && data.Length >= 1)
                return (data[0] & 1) == 0;
        }
        catch { /* fall through */ }

        // If no StartupApproved entry exists, the Run value is effectively enabled.
        return true;
    }

    private static void ScanStartupFolder(StartupSource source, string section, List<StartupEntry> entries)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            // Materialise inside a guard: enumeration is lazy, so an I/O error surfacing
            // mid-foreach would otherwise escape Scan and crash the page's async handler.
            string[] files;
            try { files = Directory.EnumerateFiles(folder).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                // Only include executables and shortcuts; skip desktop.ini etc.
                if (ext is not (".exe" or ".lnk" or ".bat" or ".cmd" or ".vbs" or ".ps1")) continue;
                var enabled = IsFolderEntryEnabled(Path.GetFileName(file));
                entries.Add(new StartupEntry(
                    name,
                    file,
                    ext == ".exe" ? file : null,
                    source,
                    enabled,
                    section,
                    FilePath: file));
            }
        }
    }

    private static bool IsFolderEntryEnabled(string fileName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var approved = root.OpenSubKey(ExplorerStartupFolderKey);
            if (approved?.GetValue(fileName) is byte[] data && data.Length >= 1)
                return (data[0] & 1) == 0;
        }
        catch { /* fall through */ }
        return true;
    }

    /// <summary>Disables or enables a startup entry. For registry entries this
    /// toggles the StartupApproved value in the entry's own hive (all-users
    /// entries therefore need administrator rights); for startup
    /// folder entries it toggles the StartupApproved\StartupFolder flag so the
    /// file itself is never modified.</summary>
    public bool ToggleEnabled(StartupEntry entry, bool enable, out string? error)
    {
        error = null;
        if (entry.Source is StartupSource.StartupFolder)
        {
            if (ToggleFolderEntry(entry, enable)) return true;
            error = "The startup folder setting could not be changed.";
            return false;
        }
        return ToggleRegistryEntry(entry, enable, out error);
    }

    private static bool ToggleRegistryEntry(StartupEntry entry, bool enable, out string? error)
    {
        error = null;
        try
        {
            // The approval lives in the same hive as the Run value it governs:
            // that is where Explorer (and Task Manager) read it, and where Scan
            // reads it back. Writing an all-users entry's approval under HKCU was
            // a no-op that reported success - the toggle snapped back on the next
            // scan and the program kept starting. HKLM needs elevation, so for an
            // unelevated app that write is refused and the UI reverts the toggle.
            var hive = entry.Source == StartupSource.RegistryLocalMachine
                ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var approvalPath = ApprovalPathFor(entry);
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = root.OpenSubKey(approvalPath, writable: true)
                ?? root.CreateSubKey(approvalPath, writable: true);
            var valueName = entry.ValueName;
            if (approved is null || string.IsNullOrEmpty(valueName))
            {
                error = "The startup setting could not be opened.";
                return false;
            }

            if (enable)
                approved.DeleteValue(valueName, throwOnMissingValue: false);
            else
                approved.SetValue(valueName, DisabledApproval, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       && entry.Source == StartupSource.RegistryLocalMachine)
        {
            // Same wording as Remove: an unelevated app cannot change an
            // all-users entry, and "could not disable" alone did not say why.
            error = "Administrator rights are required to change entries that apply to all users. Run CleanMachine as administrator, or use Task Manager > Startup apps.";
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected registry failure: report it so the UI reverts the toggle.
            error = ex.Message;
            return false;
        }
    }

    private static bool ToggleFolderEntry(StartupEntry entry, bool enable)
    {
        try
        {
            var fileName = Path.GetFileName(entry.FilePath ?? entry.Command);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var approved = root.CreateSubKey(ExplorerStartupFolderKey, writable: true);
            if (approved is null || string.IsNullOrEmpty(fileName)) return false;

            if (enable)
                approved.DeleteValue(fileName, throwOnMissingValue: false);
            else
                approved.SetValue(fileName, DisabledApproval, RegistryValueKind.Binary);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Removes a startup entry entirely: deletes the registry value or
    /// deletes the file from the startup folder. All-users entries require
    /// administrator rights and are refused with a clear message rather than
    /// attempted silently.</summary>
    public bool Remove(StartupEntry entry, out string? error)
    {
        error = null;

        if (entry.Source is StartupSource.StartupFolder)
        {
            var path = entry.FilePath ?? entry.Command;
            if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StringComparison.OrdinalIgnoreCase))
            {
                error = "Administrator rights are required to remove entries from the all-users startup folder. Disable it instead, or run CleanMachine as administrator.";
                return false;
            }
            try
            {
                var startupRoot = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                SafeDeletion.DeleteFile(path, startupRoot);
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                    using var approved = root.OpenSubKey(ExplorerStartupFolderKey, writable: true);
                    approved?.DeleteValue(Path.GetFileName(path), throwOnMissingValue: false);
                }
                catch { /* approval cleanup is best-effort */ }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        if (entry.Source is StartupSource.RegistryLocalMachine)
        {
            error = "Administrator rights are required to remove entries that apply to all users. Disable it instead, or run CleanMachine as administrator.";
            return false;
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            var keyPath = entry.RegistryPath.EndsWith("RunOnce", StringComparison.Ordinal) ? RunOnceKey : RunKey;
            var valueName = entry.ValueName;
            if (string.IsNullOrEmpty(valueName)) return false;

            // A null key handle used to make this a silent no-op that still
            // reported success, so a failed removal looked exactly like a
            // successful one - and the entry was still there on the next scan.
            // Refuse rather than claim to have done something we did not do.
            using (var runKey = root.OpenSubKey(keyPath, writable: true))
            {
                if (runKey is null)
                {
                    error = $"The startup key {keyPath} could not be opened for writing.";
                    return false;
                }
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            // Confirm the value is really gone rather than trusting the call.
            // Something else re-creating the value later is outside our control,
            // but a removal that did not take effect must not be reported as done.
            using (var verify = root.OpenSubKey(keyPath, writable: false))
            {
                if (verify?.GetValue(valueName) is not null)
                {
                    error = $"The startup entry \"{valueName}\" could not be removed from {keyPath}.";
                    return false;
                }
            }

            using (var approved = root.OpenSubKey(keyPath.EndsWith("RunOnce", StringComparison.Ordinal) ? ExplorerRunOnceKey : ExplorerRunKey, writable: true))
                approved?.DeleteValue(valueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Strips the GUID suffix that Windows appends to UWP / packaged app
    /// auto-start names (e.g. "MicrosoftCopilotAutoLaunch_3BE43B7D...").</summary>
    private static string FriendlyName(string raw)
    {
        // Pattern: DisplayName_GuidHex
        var underscore = raw.LastIndexOf('_');
        if (underscore > 0 && raw.Length - underscore - 1 >= 32)
        {
            var guid = raw[(underscore + 1)..];
            if (guid.All(c => char.IsAsciiHexDigit(c) || c == '-'))
                return raw[..underscore];
        }
        return raw;
    }

    /// <summary>Extracts a usable executable path from a registry Run command string.
    /// Shares the Registry Care resolver so the Startup Apps page and the dead-entry
    /// scanner can never disagree about which entries point at a missing program.</summary>
    internal static string? ResolveExecutable(string command)
        => CleanupService.ResolveStartupExecutable(command);
}
