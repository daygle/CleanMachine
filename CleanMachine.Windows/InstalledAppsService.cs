using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

public enum AppEntryKind { Win32, Store }

/// <summary>A single installed application: a Win32 uninstall registry entry or a Microsoft Store package.</summary>
public sealed record InstalledApp(
    string Name,
    string Version,
    string Publisher,
    string? InstallDate,
    long? EstimatedSize,
    bool IsSystemComponent,
    bool IsProtected,
    string UninstallCommand,
    string QuietUninstallCommand,
    string ModifyCommand,
    string RegistryKey,
    string Architecture,
    AppEntryKind Kind = AppEntryKind.Win32,
    string PackageFullName = "");

/// <summary>Enumerates installed applications: Win32 entries from the uninstall
/// registry keys (both registry views) and Microsoft Store packages from the
/// deployment API. Uninstall and Modify hand control to the vendor's own
/// uninstaller; CleanMachine never removes another program's files itself.</summary>
public sealed class InstalledAppsService
{
    private static readonly string[] UninstallRoots =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IReadOnlyList<InstalledApp> Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<InstalledApp>();

        // HKLM 64-bit view (all users)
        ScanHive(RegistryHive.LocalMachine, RegistryView.Registry64, results, seen);
        // HKCU (current user)
        ScanHive(RegistryHive.CurrentUser, RegistryView.Default, results, seen);
        // Store packages
        CollectStoreApps(results, seen);

        return results
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanHive(RegistryHive hive, RegistryView view, List<InstalledApp> results, HashSet<string> seen)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var rootPath in UninstallRoots)
            {
                using var key = root.OpenSubKey(rootPath);
                if (key is null) continue;
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey is null) continue;

                    var name = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var systemComponent = GetInt(subKey, "SystemComponent") == 1;
                    var releaseType = subKey.GetValue("ReleaseType") as string ?? "";
                    var parentName = subKey.GetValue("ParentDisplayName") as string;

                    // Skip hidden Windows updates and patch packages
                    if (string.Equals(releaseType, "Update", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(releaseType, "Hotfix", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip child entries (they appear under their parent)
                    if (!string.IsNullOrWhiteSpace(parentName)) continue;

                    // Deduplicate: the same app can appear in both views/hives.
                    var version = subKey.GetValue("DisplayVersion") as string ?? "";
                    if (!seen.Add($"{name}|{version}")) continue;

                    var estimatedSize = GetInt(subKey, "EstimatedSize");
                    var uninstallCmd = subKey.GetValue("UninstallString") as string ?? "";
                    var quietUninstallCmd = subKey.GetValue("QuietUninstallString") as string ?? "";
                    var modifyCmd = subKey.GetValue("ModifyPath") as string
                        ?? subKey.GetValue("ModifyString") as string
                        ?? subKey.GetValue("Modify") as string
                        ?? "";
                    var publisher = subKey.GetValue("Publisher") as string ?? "";

                    var arch = rootPath.Contains("Wow6432Node", StringComparison.OrdinalIgnoreCase)
                        ? "32-bit"
                        : DetectArchitecture(subKey);

                    var installDate = subKey.GetValue("InstallDate") as string;

                    results.Add(new InstalledApp(
                        CleanName(name),
                        version,
                        CleanPublisher(publisher),
                        FormatInstallDate(installDate),
                        estimatedSize > 0 ? estimatedSize * 1024L : null,
                        systemComponent,
                        IsProtected(subKey),
                        uninstallCmd,
                        quietUninstallCmd,
                        modifyCmd,
                        $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{rootPath}\{subKeyName}",
                        arch));
                }
            }
        }
        catch { /* inaccessible hive - skip silently */ }
    }

    private static void CollectStoreApps(List<InstalledApp> results, HashSet<string> seen)
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            var packages = manager.FindPackagesForUser(string.Empty);
            foreach (var package in packages)
            {
                try
                {
                    var name = package.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = package.Id.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!seen.Add($"{name}|Store")) continue;

                    var version = package.Id.Version;
                    results.Add(new InstalledApp(
                        Name: name,
                        Version: $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        Publisher: CleanPublisher(package.Id.Publisher ?? ""),
                        InstallDate: null,
                        EstimatedSize: null,
                        IsSystemComponent: package.IsFramework || package.IsResourcePackage,
                        IsProtected: false,
                        UninstallCommand: "",
                        QuietUninstallCommand: "",
                        ModifyCommand: "",
                        RegistryKey: $"Store\\{package.Id.FullName}",
                        Architecture: "Store",
                        Kind: AppEntryKind.Store,
                        PackageFullName: package.Id.FullName));
                }
                catch { /* a malformed package entry is skipped */ }
            }
        }
        catch
        {
            // Store enumeration can fail in restricted environments; the Win32
            // list is still shown.
        }
    }

    /// <summary>Cleans up display names: strips GUID suffixes, normalizes whitespace.</summary>
    private static string CleanName(string name)
    {
        // Strip trailing GUIDs like "App_{GUID}"
        var braceStart = name.LastIndexOf('{');
        if (braceStart > 0 && name.TrimEnd().EndsWith("}"))
        {
            var trimmed = name[..braceStart].TrimEnd('_', ' ');
            if (trimmed.Length > 0) return trimmed;
        }
        return name.Trim();
    }

    /// <summary>Extracts the readable publisher name from X.500 distinguished name strings
    /// like "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US".</summary>
    private static string CleanPublisher(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // Try to extract "O=" (Organization) first, then "CN=" (Common Name)
        var oMatch = ExtractDnField(raw, "O=");
        var cnMatch = ExtractDnField(raw, "CN=");
        var lMatch = ExtractDnField(raw, "L=");

        var publisher = !string.IsNullOrWhiteSpace(oMatch) ? oMatch : cnMatch;
        if (string.IsNullOrWhiteSpace(publisher)) return raw;

        // Append location if available
        if (!string.IsNullOrWhiteSpace(lMatch))
            return $"{publisher} ({lMatch})";

        return publisher;
    }

    private static string? ExtractDnField(string dn, string field)
    {
        var idx = dn.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + field.Length;
        // Find the next comma or end of string
        var end = dn.IndexOf(',', start);
        if (end < 0) end = dn.Length;
        return dn[start..end].Trim();
    }

    private static string FormatInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        // Registry stores as "yyyyMMdd"
        if (raw.Length == 8 && int.TryParse(raw, out _))
        {
            if (DateTime.TryParseExact(raw, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var date))
                return date.ToString("yyyy-MM-dd");
        }
        return raw;
    }

    private static string DetectArchitecture(RegistryKey subKey)
    {
        // Windows Installer uses "WindowsInstaller" + "InstallerVersion"
        var installer = GetInt(subKey, "WindowsInstaller");
        if (installer == 1) return "MSI";

        return "";
    }

    private static bool IsProtected(RegistryKey subKey)
    {
        return GetInt(subKey, "NoRemove") == 1
            || GetInt(subKey, "SystemComponent") == 1;
    }

    private static int GetInt(RegistryKey key, string name)
    {
        try
        {
            var val = key.GetValue(name);
            if (val is int i) return i;
            if (val is byte[] b && b.Length >= 4)
                return BitConverter.ToInt32(b, 0);
        }
        catch { }
        return 0;
    }

    /// <summary>Launches the modify installer for the given app.</summary>
    public bool LaunchModify(InstalledApp app)
    {
        if (app.Kind == AppEntryKind.Store) return false;

        var cmd = app.ModifyCommand;
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        return LaunchCommand(cmd);
    }

    /// <summary>Launches the uninstaller for the given app. Returns true if started.
    /// Store packages are removed through the deployment API.</summary>
    public bool LaunchUninstall(InstalledApp app, bool quiet = false)
    {
        if (app.Kind == AppEntryKind.Store)
        {
            try
            {
                var manager = new global::Windows.Management.Deployment.PackageManager();
                var package = manager.FindPackagesForUser(string.Empty)
                    .FirstOrDefault(p => p.Id.FullName == app.PackageFullName);
                if (package is null) return false;
                manager.RemovePackageAsync(package.Id.FullName).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch { return false; }
        }

        var cmd = quiet && !string.IsNullOrWhiteSpace(app.QuietUninstallCommand)
            ? app.QuietUninstallCommand
            : app.UninstallCommand;

        if (string.IsNullOrWhiteSpace(cmd)) return false;

        return LaunchCommand(cmd);
    }

    /// <summary>Starts a vendor command, splitting executable path from arguments
    /// correctly for both quoted and unquoted command strings.</summary>
    private static bool LaunchCommand(string command)
    {
        try
        {
            var trimmed = command.Trim();
            string fileName;
            string arguments = "";

            if (trimmed.StartsWith('"'))
            {
                var end = trimmed.IndexOf('"', 1);
                if (end < 0) return false;
                fileName = trimmed[1..end];
                arguments = trimmed[(end + 1)..].Trim();
            }
            else
            {
                var space = trimmed.IndexOf(' ');
                if (space < 0)
                {
                    fileName = trimmed;
                }
                else
                {
                    // Grow the prefix to the next space until it names an existing
                    // file: the first (shortest) existing prefix is the executable
                    // itself for unquoted "C:\Program Files\..." paths with spaces,
                    // while any longer prefix would already swallow the arguments.
                    fileName = trimmed[..space];
                    while (space > 0)
                    {
                        var candidate = trimmed[..space];
                        if (File.Exists(candidate)) { fileName = candidate; break; }
                        var next = trimmed.IndexOf(' ', space + 1);
                        if (next < 0) break;
                        space = next;
                    }
                    arguments = trimmed[fileName.Length..].Trim();
                }
            }

            if (fileName.Length == 0) return false;

            // Let the shell resolve the rest (e.g. msiexec, rundll32 templates).
            if (!Path.IsPathRooted(fileName) && !File.Exists(fileName))
            {
                Process.Start(new ProcessStartInfo(trimmed) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
