using Microsoft.Win32;

namespace CleanMachine.Windows;

public enum StartupKind { RegistryValue, StartupFolderFile }

public sealed record StartupKeyInfo(string HiveLabel, bool IsCurrentUser, string SubKey, string ValueName, string ApprovalSubKey);

/// <summary>One auto-start entry found on the machine.</summary>
public sealed record StartupApp(
    string Id,
    string Name,
    string Command,
    string LocationLabel,
    StartupKind Kind,
    StartupKeyInfo? Key,
    string? FilePath,
    bool Enabled);

/// <summary>Manages per-user startup applications: enumeration, enable/disable and
/// removal. Enable/disable uses the same Explorer StartupApproved convention as
/// Task Manager, so changes made here are visible there (and vice versa) and never
/// require administrator rights. Removing entries that apply to all users (HKLM Run
/// values, the common startup folder) does require elevation and is refused with a
/// clear message rather than attempted.</summary>
public sealed class StartupAppsService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovalRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovalRunOnce = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\RunOnce";
    private const string ApprovalStartupFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private static readonly string UserStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static readonly string CommonStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    /// <summary>Binary value Explorer writes for a disabled entry (first byte odd).</summary>
    private static readonly byte[] DisabledApproval = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public Task<IReadOnlyList<StartupApp>> ScanAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<StartupApp>>(() =>
        {
            var result = new List<StartupApp>();
            CollectRunEntries(RegistryHive.CurrentUser, "HKCU", RunPath, result);
            CollectRunEntries(RegistryHive.CurrentUser, "HKCU", RunOncePath, result);
            CollectRunEntries(RegistryHive.LocalMachine, "HKLM", RunPath, result);
            CollectRunEntries(RegistryHive.LocalMachine, "HKLM", RunOncePath, result);
            CollectFolderEntries(UserStartupFolder, "Startup folder", isCommon: false, result);
            CollectFolderEntries(CommonStartupFolder, "Common startup folder (all users)", isCommon: true, result);
            return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }, token);

    private static void CollectRunEntries(RegistryHive hive, string hiveLabel, string subKey, List<StartupApp> result)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = root.OpenSubKey(subKey);
            if (key is null) return;
            var isCurrentUser = hive == RegistryHive.CurrentUser;
            var approvalSubKey = subKey.EndsWith("RunOnce", StringComparison.Ordinal) ? ApprovalRunOnce : ApprovalRun;
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    if (key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                }
                catch { continue; }
                var command = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(command)) continue;
                result.Add(new StartupApp(
                    $"{hiveLabel}|{subKey}|{name}",
                    name,
                    command,
                    isCurrentUser ? $"Registry ({hiveLabel})" : $"Registry ({hiveLabel}, all users)",
                    StartupKind.RegistryValue,
                    new StartupKeyInfo(hiveLabel, isCurrentUser, subKey, name, approvalSubKey),
                    null,
                    Enabled: !IsHiddenByApproval(approvalSubKey, name)));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // An unreadable hive section is skipped; the rest of the list still shows.
        }
    }

    private static void CollectFolderEntries(string folder, string label, bool isCommon, List<StartupApp> result)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(f => Path.GetExtension(f) is ".lnk" or ".exe" or ".bat" or ".cmd")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                result.Add(new StartupApp(
                    $"Folder|{file}",
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    label,
                    StartupKind.StartupFolderFile,
                    null,
                    file,
                    Enabled: !IsHiddenByApproval(ApprovalStartupFolder, name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True when the Explorer StartupApproved flag marks the entry as
    /// disabled (the first byte of the binary value is odd).</summary>
    private static bool IsHiddenByApproval(string approvalSubKey, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = root.OpenSubKey(approvalSubKey);
            return key?.GetValue(valueName) is byte[] data && data.Length > 0 && (data[0] & 1) == 1;
        }
        catch { return false; }
    }

    /// <summary>Enables or disables an entry by writing/removing the Explorer
    /// StartupApproved flag (per-user, no elevation needed).</summary>
    public Task ToggleAsync(StartupApp app, CancellationToken token = default) => Task.Run(() =>
    {
        var approvalSubKey = app.Kind == StartupKind.RegistryValue ? app.Key!.ApprovalSubKey : ApprovalStartupFolder;
        var valueName = app.Kind == StartupKind.RegistryValue ? app.Key!.ValueName : Path.GetFileName(app.FilePath!);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var approval = root.CreateSubKey(approvalSubKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the startup approval key.");
        if (app.Enabled)
            approval.SetValue(valueName, DisabledApproval, RegistryValueKind.Binary);
        else
            approval.DeleteValue(valueName, throwOnMissingValue: false);
    }, token);

    /// <summary>Permanently removes an entry. Current-user entries and startup
    /// folder items are removed directly; all-users entries require elevation and
    /// are refused with a clear message.</summary>
    public Task DeleteAsync(StartupApp app, CancellationToken token = default) => Task.Run(() =>
    {
        if (app.Kind == StartupKind.RegistryValue)
        {
            var info = app.Key!;
            if (!info.IsCurrentUser)
                throw new InvalidOperationException(
                    "Administrator rights are required to remove this entry because it applies to all users. Disable it instead, or run CleanMachine as administrator.");
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = root.OpenSubKey(info.SubKey, writable: true)
                ?? throw new InvalidOperationException("The registry key for this entry no longer exists.");
            key.DeleteValue(info.ValueName, throwOnMissingValue: false);
            using var approval = root.OpenSubKey(info.ApprovalSubKey, writable: true);
            approval?.DeleteValue(info.ValueName, throwOnMissingValue: false);
        }
        else
        {
            var path = app.FilePath!;
            if (path.StartsWith(CommonStartupFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Administrator rights are required to remove entries from the common startup folder. Disable it instead, or run CleanMachine as administrator.");
            File.Delete(path);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var approval = root.OpenSubKey(ApprovalStartupFolder, writable: true);
            approval?.DeleteValue(Path.GetFileName(path), throwOnMissingValue: false);
        }
    }, token);
}
