using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

public enum AppEntryKind { Win32, Store }

/// <summary>One installed application (Win32 uninstall entry or Store package).</summary>
public sealed record InstalledApp(
    string Id,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    long EstimatedSizeBytes,
    string? InstallDate,
    AppEntryKind Kind,
    string? UninstallString,
    string? ModifyPath,
    bool SystemComponent,
    string Scope);

/// <summary>Enumerates installed applications for the Installed Apps page: Win32
/// entries from the standard uninstall registry keys (both registry views, with
/// HKLM entries flagged) and Store packages from the deployment API. Uninstall and
/// Modify hand control to the vendor's own uninstaller; the app never removes
/// another program's files itself.</summary>
public sealed class InstalledAppsService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledApp>> ScanAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<InstalledApp>>(() =>
        {
            var result = new List<InstalledApp>();
            CollectWin32Apps(RegistryHive.LocalMachine, RegistryView.Registry64, "All users", result);
            CollectWin32Apps(RegistryHive.LocalMachine, RegistryView.Registry32, "All users (32-bit)", result);
            CollectWin32Apps(RegistryHive.CurrentUser, RegistryView.Default, "Current user", result);
            CollectStoreApps(result);
            return result
                .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
                .GroupBy(a => $"{a.DisplayName}|{a.DisplayVersion}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, token);

    private static void CollectWin32Apps(RegistryHive hive, RegistryView view, string scope, List<InstalledApp> result)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                // MSI product-code keys surface through their DisplayName; entries
                // without one (orphaned product codes) are skipped by the check below.
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry is null) continue;
                    var displayName = entry.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    var uninstallString = entry.GetValue("UninstallString") as string;
                    var modifyPath = entry.GetValue("ModifyPath") as string;
                    var isSystemComponent = (entry.GetValue("SystemComponent") as int? ?? 0) != 0;

                    long size = 0;
                    if (entry.GetValue("EstimatedSize") is int kb) size = kb * 1024L;

                    string? installDate = null;
                    if (entry.GetValue("InstallDate") is string raw && raw.Length == 8 && raw.All(char.IsDigit))
                    {
                        // Registry convention: yyyymmdd.
                        installDate = $"{raw[..4]}-{raw[4..6]}-{raw[6..]}";
                    }

                    result.Add(new InstalledApp(
                        Id: $"{hive}|{view}|{name}",
                        DisplayName: displayName!,
                        DisplayVersion: entry.GetValue("DisplayVersion") as string,
                        Publisher: entry.GetValue("Publisher") as string,
                        EstimatedSizeBytes: size,
                        InstallDate: installDate,
                        Kind: AppEntryKind.Win32,
                        UninstallString: string.IsNullOrWhiteSpace(uninstallString) ? null : uninstallString,
                        ModifyPath: string.IsNullOrWhiteSpace(modifyPath) ? null : modifyPath,
                        SystemComponent: isSystemComponent,
                        Scope: scope));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException) { }
    }

    private static void CollectStoreApps(List<InstalledApp> result)
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
                    var version = package.Id.Version;
                    result.Add(new InstalledApp(
                        Id: $"Store|{package.Id.FullName}",
                        DisplayName: name,
                        DisplayVersion: $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        Publisher: package.Publisher,
                        EstimatedSizeBytes: 0,
                        InstallDate: null,
                        Kind: AppEntryKind.Store,
                        UninstallString: null,
                        ModifyPath: null,
                        SystemComponent: package.IsFramework || package.IsResourcePackage,
                        Scope: "Store"));
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

    /// <summary>Launches the vendor's own uninstaller. HKLM-based uninstallers will
    /// request elevation through Windows' own prompt.</summary>
    public Task LaunchUninstallAsync(InstalledApp app, CancellationToken token = default) => Task.Run(() =>
    {
        if (app.Kind == AppEntryKind.Store)
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            var package = manager.FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => p.Id.FullName == app.Id["Store|".Length..])
                ?? throw new InvalidOperationException("This package is no longer installed.");
            manager.RemovePackageAsync(package.Id.FullName).AsTask(token).GetAwaiter().GetResult();
            return;
        }

        var command = app.UninstallString
            ?? throw new InvalidOperationException("This application does not expose an uninstaller.");
        LaunchCommand(command);
    }, token);

    /// <summary>Launches the vendor's change/repair program when one exists.</summary>
    public Task LaunchModifyAsync(InstalledApp app, CancellationToken token = default) => Task.Run(() =>
    {
        if (app.Kind == AppEntryKind.Store)
            throw new InvalidOperationException("Store packages do not expose a modify program.");
        var command = app.ModifyPath
            ?? throw new InvalidOperationException("This application does not expose a modify program.");
        LaunchCommand(command);
    }, token);

    private static void LaunchCommand(string command)
    {
        var trimmed = command.Trim();
        string fileName;
        string arguments = string.Empty;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0) throw new InvalidOperationException("The uninstall command is malformed.");
            fileName = trimmed[1..end];
            arguments = trimmed[(end + 1)..].Trim();
        }
        else
        {
            // Unquoted command: the executable is everything up to the first space
            // that ends a real path (conservative split; vendors usually quote).
            var space = trimmed.IndexOf(' ');
            if (space < 0)
            {
                fileName = trimmed;
            }
            else
            {
                fileName = trimmed[..space];
                arguments = trimmed[(space + 1)..].Trim();
            }
        }

        if (fileName.Length == 0 || !File.Exists(fileName))
            throw new InvalidOperationException("The uninstaller executable could not be found.");

        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true
        });
    }
}
