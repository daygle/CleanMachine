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
    bool IsOrphan = false);

public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    StartupFolder
}

/// <summary>Enumerates and manages Windows startup entries from the Run keys and
/// the per-user and all-users Startup folders.</summary>
public sealed class StartupAppsService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ExplorerRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

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
            using var runKey = root.OpenSubKey(RunKey);
            if (runKey is null) return;

            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                var command = runKey.GetValue(valueName) as string ?? "";
                var exePath = ResolveExecutable(command);
                var enabled = IsRegistryEntryEnabled(root, valueName);
                var isOrphan = exePath is not null && !File.Exists(exePath);
                entries.Add(new StartupEntry(
                    FriendlyName(valueName),
                    command,
                    exePath,
                    source,
                    enabled,
                    section,
                    isOrphan));
            }
        }
        catch { /* inaccessible hive – skip silently */ }
    }

    private static bool IsRegistryEntryEnabled(RegistryKey root, string valueName)
    {
        // The StartupApproved\Run key stores a binary value where the first byte
        // determines enabled (0x02 or 0x06) vs disabled (0x03).
        try
        {
            using var approved = root.OpenSubKey(ExplorerRunKey);
            if (approved?.GetValue(valueName) is byte[] data && data.Length >= 1)
                return data[0] is 0x02 or 0x06;
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
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\Startup")
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
                entries.Add(new StartupEntry(
                    name,
                    file,
                    ext == ".exe" ? file : null,
                    source,
                    true,
                    section));
            }
        }
    }

    /// <summary>Disables or enables a startup entry. For registry entries this
    /// toggles the StartupApproved value; for startup folder entries it renames
    /// the file with a leading dot to hide it from Explorer.</summary>
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
            var hive = entry.Source == StartupSource.RegistryCurrentUser
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine;

            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var approved = root.OpenSubKey(ExplorerRunKey, writable: true);

            // Find the original value name from the command (reverse-engineer from the friendly display).
            var valueName = FindOriginalValueName(root, entry);
            if (valueName is null) return false;

            if (enable)
            {
                // Delete the StartupApproved entry so the Run value is active again.
                approved?.DeleteValue(valueName, false);
            }
            else
            {
                // Write a disabled marker (0x03).
                approved?.SetValue(valueName, new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            return true;
        }
        catch { return false; }
    }

    private static string? FindOriginalValueName(RegistryKey root, StartupEntry entry)
    {
        using var runKey = root.OpenSubKey(RunKey);
        if (runKey is null) return null;
        foreach (var name in runKey.GetValueNames())
        {
            var cmd = runKey.GetValue(name) as string ?? "";
            if (string.Equals(FriendlyName(name), entry.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cmd, entry.Command, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    private static bool ToggleFolderEntry(StartupEntry entry, bool enable)
    {
        try
        {
            var path = entry.Command;
            if (!File.Exists(path) && !enable)
            {
                // Check for the dotted version.
                var dotted = DotPath(path);
                if (File.Exists(dotted)) path = dotted;
                else return false;
            }

            if (enable)
            {
                var dotted = DotPath(path);
                if (File.Exists(dotted) && !File.Exists(path))
                    File.Move(dotted, path);
            }
            else
            {
                if (File.Exists(path) && !File.Exists(DotPath(path)))
                    File.Move(path, DotPath(path));
            }
            return true;
        }
        catch { return false; }
    }

    private static string DotPath(string path) => Path.Combine(Path.GetDirectoryName(path) ?? ".", "." + Path.GetFileName(path));

    /// <summary>Removes a startup entry entirely: deletes the registry value or
    /// deletes the file from the startup folder.</summary>
    public bool Remove(StartupEntry entry)
    {
        if (entry.Source is StartupSource.StartupFolder)
        {
            try { File.Delete(entry.Command); return true; } catch { return false; }
        }

        try
        {
            var hive = entry.Source == StartupSource.RegistryCurrentUser
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

            var valueName = FindOriginalValueName(root, entry);
            if (valueName is null) return false;

            using (var runKey = root.OpenSubKey(RunKey, writable: true))
                runKey?.DeleteValue(valueName, false);
            using (var approved = root.OpenSubKey(ExplorerRunKey, writable: true))
                approved?.DeleteValue(valueName, false);

            return true;
        }
        catch { return false; }
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
