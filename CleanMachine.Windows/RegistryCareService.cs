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

    private readonly CleanupService _cleanup = new();

    // The only locations a finding may be deleted from. The scanner only ever
    // produces paths under these roots; validating again at delete time means a
    // tampered or future finding can never point the cleaner somewhere else.
    private static readonly string[] AllowedCleanupRoots =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"Software\Classes\"
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
