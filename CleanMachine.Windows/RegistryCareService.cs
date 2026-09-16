using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

public sealed record RegistryReview(IReadOnlyList<RegistryFinding> Findings, IReadOnlyList<RegistryBackup> Backups);

/// <summary>Outcome of cleaning selected registry findings.</summary>
public sealed record RegistryCleanResult(int Removed, IReadOnlyList<CleanupIssue> Skipped);

public sealed class RegistryCareService
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ClassesRoot = @"Software\Classes";
    private const string MuiCacheRoot = @"Control Panel\Desktop\MuiCached";
    private const string StartupRunRoot = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRunOnceRoot = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string SoundAppsRoot = @"AppEvents\Schemes\Apps";
    private const string AppPathsRoot = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    private const string ShellMuiCacheRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const string FileExtsRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const string CompatAssistantRoot = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant";

    private readonly CleanupService _cleanup = new();

    // The only locations a finding may be deleted from. The scanner only ever
    // produces paths under these roots; validating again at delete time means a
    // tampered or future finding can never point the cleaner somewhere else.
    // All are per-user (HKCU) and self-healing or orphaned-reference safe.
    private static readonly string[] AllowedCleanupRoots =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"Software\Classes\",
        @"Control Panel\Desktop\MuiCached",
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"AppEvents\Schemes\Apps\",
        @"Software\Microsoft\Windows\CurrentVersion\App Paths\",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\"
        // Shell MuiCache is covered by the "Software\Classes\" root above.
    ];

    internal static bool IsDeletablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500) return false;
        if (path.Contains('"') || path.Contains("..", StringComparison.Ordinal)) return false;
        return AllowedCleanupRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a finding passes the safety gate for actual deletion.</summary>
    public static bool IsCleanable(RegistryFinding finding)
        => finding.LowRisk && finding.Confidence >= 70
           && finding.Hive == "HKCU" && IsDeletablePath(finding.Path);

    /// <summary>A short, human-readable label for a finding, for list UIs.</summary>
    public static string DisplayName(RegistryFinding finding)
    {
        if (finding.Path.StartsWith(UninstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            var name = finding.Path[UninstallRoot.Length..].TrimStart('\\');
            return string.IsNullOrEmpty(name) ? "Leftover uninstall entry" : $"Leftover program: {name}";
        }
        // Shell MuiCache lives under Software\Classes but is a display-name cache,
        // not a file association - check it before the Classes branch below.
        if (finding.Path.Equals(ShellMuiCacheRoot, StringComparison.OrdinalIgnoreCase))
            return "Shell cache entry";
        if (finding.Path.StartsWith(AppPathsRoot + @"\", StringComparison.OrdinalIgnoreCase))
            return $"App Paths: {LeafKeyName(finding.Path)}";
        if (finding.Path.StartsWith(FileExtsRoot + @"\", StringComparison.OrdinalIgnoreCase))
        {
            var ext = finding.Path[(FileExtsRoot.Length + 1)..].Split('\\')[0];
            return $"Open-with entry: {ext}";
        }
        if (finding.Path.StartsWith(CompatAssistantRoot + @"\", StringComparison.OrdinalIgnoreCase))
            return "Compatibility record";
        if (finding.Path.StartsWith(ClassesRoot, StringComparison.OrdinalIgnoreCase))
        {
            var ext = finding.Path[ClassesRoot.Length..].TrimStart('\\');
            return string.IsNullOrEmpty(ext) ? "File association" : $"File association: {ext}";
        }
        if (finding.Path.StartsWith(MuiCacheRoot, StringComparison.OrdinalIgnoreCase))
            return "Localized UI cache (MUI)";
        if (finding.Path == StartupRunRoot || finding.Path == StartupRunOnceRoot)
            return finding.ValueName is null ? "Startup entry" : $"Startup entry: {finding.ValueName}";
        if (finding.Path.StartsWith(SoundAppsRoot + @"\", StringComparison.OrdinalIgnoreCase))
            return "Orphaned sound event";
        return finding.Path;
    }

    public async Task<RegistryReview> ScanAsync(CancellationToken token = default)
        => new((await _cleanup.ScanRegistrySafelyAsync(token))
            .OrderByDescending(f => f.Confidence)
            .Take(250)
            .ToArray(), []);

    public async Task<RegistryReview> PrepareReviewAsync(
        IEnumerable<RegistryFinding> selected,
        CancellationToken token = default)
    {
        var safe = selected
            .Where(f => f.LowRisk && f.Confidence >= 70)
            .Take(250)
            .ToArray();
        if (safe.Length == 0)
            return new RegistryReview([], []);

        List<RegistryBackup> backups;
        try
        {
            backups = await CreateBackupsAsync(safe, token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Best-effort: if we cannot back up (fresh profile, no permissions,
            // reg.exe unavailable), surface an empty review so the caller can
            // refuse to clean without a restore point.
            backups = [];
        }
        return new RegistryReview(safe, backups);
    }

    /// <summary>Deletes the key each cleanable finding points at. Every deletion is
    /// gated by IsCleanable and requires the key to still exist; anything else is
    /// reported in Skipped rather than acted on.</summary>
    public Task<RegistryCleanResult> CleanAsync(RegistryReview review, CancellationToken token = default)
    {
        var removed = 0;
        var skipped = new List<CleanupIssue>();
        foreach (var finding in review.Findings)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCleanable(finding)) { skipped.Add(new(finding.Path, "Not eligible (safety gate)")); continue; }
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                if (finding.ValueName is not null)
                {
                    // Value-level finding: delete a single named value under the key.
                    using var key = root.OpenSubKey(finding.Path, writable: true);
                    if (key is null) { skipped.Add(new(finding.Path, "Key not found")); continue; }
                    if (key.GetValue(finding.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
                    {
                        skipped.Add(new(finding.Path, "Value not found (already clean)"));
                        continue;
                    }
                    key.DeleteValue(finding.ValueName, throwOnMissingValue: false);
                    removed++;
                }
                else
                {
                    using var parent = root.OpenSubKey(ParentKeyPath(finding.Path), writable: true);
                    if (parent is null) { skipped.Add(new(finding.Path, "Parent key not found")); continue; }
                    var leaf = LeafKeyName(finding.Path);
                    // Probe with a scoped handle and dispose it BEFORE deleting: a key
                    // cannot be removed while any handle to it is open.
                    using (var existing = parent.OpenSubKey(leaf))
                    {
                        if (existing is null) { skipped.Add(new(finding.Path, "Key not found (already clean)")); continue; }
                    }
                    parent.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
            {
                skipped.Add(new(finding.Path, ex.Message));
            }
        }
        return Task.FromResult(new RegistryCleanResult(removed, skipped));
    }

    private static string ParentKeyPath(string path) => path[..path.LastIndexOf('\\')];
    private static string LeafKeyName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>Exports the registry scopes the given findings will be deleted from:
    /// one whole-root export for Uninstall findings, one per-key export for
    /// Software\\Classes findings (that root is far too large to export whole).</summary>
    private static async Task<List<RegistryBackup>> CreateBackupsAsync(
        IReadOnlyList<RegistryFinding> findings, CancellationToken token)
    {
        var backups = new List<RegistryBackup>();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CleanMachine", "Backups");
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (findings.Any(f => f.Path.StartsWith(UninstallRoot + @"\", StringComparison.OrdinalIgnoreCase)))
            backups.Add(await ExportKeyAsync(UninstallRoot,
                Path.Combine(directory, $"registry-uninstall-{stamp}.reg"), token));

        foreach (var path in findings
                     .Select(f => f.Path)
                     .Where(p => p.StartsWith(ClassesRoot + @"\", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = new string(path[(ClassesRoot.Length + 1)..]
                .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            backups.Add(await ExportKeyAsync(path,
                Path.Combine(directory, $"registry-classes-{name}-{stamp}.reg"), token));
        }

        // Safe per-user categories: export the key that holds the finding (or the
        // key itself for the MUI cache). These are small keys, so a whole-key export
        // is cheap and gives a precise restore point.
        foreach (var path in findings
                     .Select(f => f.Path)
                     .Where(p => p.StartsWith(MuiCacheRoot, StringComparison.OrdinalIgnoreCase)
                         || p == StartupRunRoot || p == StartupRunOnceRoot
                         || p.StartsWith(SoundAppsRoot + @"\", StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith(AppPathsRoot + @"\", StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith(FileExtsRoot + @"\", StringComparison.OrdinalIgnoreCase)
                         || p.StartsWith(CompatAssistantRoot + @"\", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = new string(path[(path.LastIndexOf('\\') + 1)..]
                .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            backups.Add(await ExportKeyAsync(path,
                Path.Combine(directory, $"registry-{name}-{stamp}.reg"), token));
        }
        return backups;
    }

    private static async Task<RegistryBackup> ExportKeyAsync(string keyPath, string filePath, CancellationToken token)
    {
        var psi = new ProcessStartInfo("reg.exe",
            $"export \"HKCU\\{keyPath}\" \"{filePath}\" /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry export tool.");
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0 || !File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            throw new InvalidOperationException("Registry backup export failed.");

        return new RegistryBackup(filePath, DateTimeOffset.UtcNow);
    }

    public static async Task<bool> ValidateBackupAsync(
        RegistryBackup backup,
        CancellationToken token = default)
    {
        if (!File.Exists(backup.FilePath))
            return false;
        if (backup.FilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(backup.FilePath).Length == 0) return false;
            // reg.exe writes UTF-16 (with BOM); older tools write ASCII "REGEDIT4".
            // Detect the encoding from the BOM and confirm the standard header line.
            using var reader = new StreamReader(backup.FilePath, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var header = ((await reader.ReadLineAsync(token)) ?? string.Empty).Trim('\uFEFF', ' ', '\t');
            return header.StartsWith("Windows Registry Editor Version 5.00", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase);
        }
        // .txt backups are also valid (text summary format)
        return new FileInfo(backup.FilePath).Length > 0;
    }

    public static async Task RestoreBackupAsync(
        RegistryBackup backup,
        CancellationToken token = default)
    {
        if (!await ValidateBackupAsync(backup, token))
            throw new InvalidDataException("The registry backup is missing, empty, or invalid.");
        if (!backup.FilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .reg file backups can be restored through the registry importer.");

        var psi = new ProcessStartInfo("reg.exe", $"import \"{backup.FilePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry restore tool.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Registry backup restore failed.");
    }
}
