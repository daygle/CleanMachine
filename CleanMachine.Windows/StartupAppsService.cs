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
    private const string ExplorerStartupFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>Binary value Explorer writes for a disabled entry (first byte odd).</summary>
    private static readonly byte[] DisabledApproval = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        ScanRegistry(RegistryHive.CurrentUser, StartupSource.RegistryCurrentUser, "Current User", entries);
        ScanRegistry(RegistryHive.LocalMachine, StartupSource.RegistryLocalMachine, "All Users", entries);
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
                    var isOrphan = exePath is not null && !File.Exists(exePath);
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
        catch { /* inaccessible hive – skip silently */ }
    }

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
            foreach (var file in Directory.EnumerateFiles(folder))
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
    /// toggles the StartupApproved value (per-user when possible); for startup
    /// folder entries it toggles the StartupApproved\StartupFolder flag so the
    /// file itself is never modified.</summary>
    public bool ToggleEnabled(StartupEntry entry, bool enable)
    {
        if (entry.Source is StartupSource.StartupFolder)
            return ToggleFolderEntry(entry, enable);
        return ToggleRegistryEntry(entry, enable);
    }

    private static bool ToggleRegistryEntry(StartupEntry entry, bool enable)
    {
        try
        {
            // Current-user entries (and HKLM entries flagged per-user via
            // StartupApproved under HKCU) are toggled without elevation.
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var approved = root.OpenSubKey(entry.RegistryPath.EndsWith("RunOnce", StringComparison.Ordinal)
                ? ExplorerRunOnceKey : ExplorerRunKey, writable: true)
                ?? root.CreateSubKey(entry.RegistryPath.EndsWith("RunOnce", StringComparison.Ordinal)
                    ? ExplorerRunOnceKey : ExplorerRunKey, writable: true);
            if (approved is null) return false;

            var valueName = entry.ValueName;
            if (string.IsNullOrEmpty(valueName)) return false;

            if (enable)
                approved.DeleteValue(valueName, throwOnMissingValue: false);
            else
                approved.SetValue(valueName, DisabledApproval, RegistryValueKind.Binary);
            return true;
        }
        catch
        {
            // HKLM values may deny write access to the per-user approval key is
            // not the issue — the approval key is always under HKCU, so failures
            // here are unexpected; report failure so the UI can revert the toggle.
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
                File.Delete(path);
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

            using (var runKey = root.OpenSubKey(keyPath, writable: true))
                runKey?.DeleteValue(valueName, throwOnMissingValue: false);
            using (var approved = root.OpenSubKey(keyPath.EndsWith("RunOnce", StringComparison.Ordinal) ? ExplorerRunOnceKey : ExplorerRunKey, writable: true))
                approved?.DeleteValue(valueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Strips the GUID suffix that Windows appends to UWP / packaged app
    /// auto-start names (e.g. "MicrosoftCopilotAutoLaunch_3BE43B7D…").</summary>
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

    /// <summary>Extracts a usable executable path from a registry Run command string,
    /// resolving quoted paths and skipping environment variables.</summary>
    internal static string? ResolveExecutable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;

        string? candidate;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            candidate = end > 1 ? trimmed[1..end] : null;
        }
        else
        {
            candidate = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        if (candidate is null || candidate.Contains('%')) return null;
        return Path.IsPathFullyQualified(candidate) ? candidate : null;
    }
}
