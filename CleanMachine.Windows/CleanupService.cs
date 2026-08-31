using Microsoft.Win32;

namespace CleanMachine.Windows;

public sealed record CleanupResult(int ItemsRemoved, long BytesRecovered);
public sealed record RegistryFinding(string Hive, string Path, string Reason, bool LowRisk);
public sealed record RegistryBackup(string FilePath, DateTimeOffset CreatedAt);

public sealed class CleanupService
{
    public Task<CleanupResult> CleanSelectedBrowsersAsync(CancellationToken cancellationToken = default)
    {
        // Native browser profile deletion will be added per-browser. The service intentionally
        // returns a result without deleting files until explicit rules are configured.
        return Task.FromResult(new CleanupResult(0, 0));
    }

    public Task<RegistryBackup> CreateRegistryBackupAsync(IEnumerable<RegistryFinding> findings, CancellationToken cancellationToken = default)
    {
        // Backup/export implementation will use a native, atomic .reg export before mutation.
        var backupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Backups", $"registry-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.reg");
        return Task.FromResult(new RegistryBackup(backupPath, DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<RegistryFinding>> ScanRegistrySafelyAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<RegistryFinding>();
        ScanUninstallEntries(RegistryHive.CurrentUser, findings);
        return Task.FromResult<IReadOnlyList<RegistryFinding>>(findings);
    }

    private static void ScanUninstallEntries(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            var displayName = entry?.GetValue("DisplayName") as string;
            var uninstallString = entry?.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(uninstallString))
            {
                findings.Add(new RegistryFinding("HKCU", $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}", "Uninstall metadata has no removal command", true));
            }
        }
    }
}
