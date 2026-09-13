using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

/// <summary>A single installed application enumerated from the Windows registry.</summary>
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
    string Architecture);

/// <summary>Enumerates all installed applications from the Windows Uninstall registry keys.</summary>
public sealed class InstalledAppsService
{
    private static readonly string[] UninstallRoots =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IReadOnlyList<InstalledApp> Scan()
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<InstalledApp>();

        // HKLM (all users)
        ScanHive(RegistryHive.LocalMachine, results, apps);

        // HKCU (current user)
        ScanHive(RegistryHive.CurrentUser, results, apps);

        return results
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanHive(RegistryHive hive, List<InstalledApp> results, HashSet<string> seen)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
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
                    if (!seen.Add(name)) continue; // deduplicate HKLM + HKCU

                    var systemComponent = GetInt(subKey, "SystemComponent") == 1;
                    var releaseType = subKey.GetValue("ReleaseType") as string ?? "";
                    var parentName = subKey.GetValue("ParentDisplayName") as string;
                    var estimatedSize = GetInt(subKey, "EstimatedSize");
                    var uninstallCmd = subKey.GetValue("UninstallString") as string ?? "";
                    var quietUninstallCmd = subKey.GetValue("QuietUninstallString") as string ?? "";
                    var modifyCmd = subKey.GetValue("ModifyString") as string ?? subKey.GetValue("Modify") as string ?? "";
                    var publisher = subKey.GetValue("Publisher") as string ?? "";

                    // Skip hidden Windows updates and patch packages
                    if (string.Equals(releaseType, "Update", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(releaseType, "Hotfix", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip child entries (they appear under their parent)
                    if (!string.IsNullOrWhiteSpace(parentName)) continue;

                    var arch = rootPath.Contains("Wow6432Node", StringComparison.OrdinalIgnoreCase)
                        ? "32-bit"
                        : DetectArchitecture(subKey);

                    var installDate = subKey.GetValue("InstallDate") as string;

                    results.Add(new InstalledApp(
                        CleanName(name),
                        subKey.GetValue("DisplayVersion") as string ?? "",
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
        catch { /* inaccessible hive – skip silently */ }
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

        // Check for the "架构" pattern in the key name or other hints
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
        var cmd = app.ModifyCommand;
        if (string.IsNullOrWhiteSpace(cmd)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Launches the uninstaller for the given app. Returns true if started.</summary>
    public bool LaunchUninstall(InstalledApp app, bool quiet = false)
    {
        var cmd = quiet && !string.IsNullOrWhiteSpace(app.QuietUninstallCommand)
            ? app.QuietUninstallCommand
            : app.UninstallCommand;

        if (string.IsNullOrWhiteSpace(cmd)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}
