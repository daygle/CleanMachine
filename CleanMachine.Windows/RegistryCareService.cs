using Microsoft.Win32;
using System.Diagnostics;

namespace CleanMachine.Windows;

/// <summary>Findings plus the backups covering them. When <see cref="Backups"/> is
/// empty despite <see cref="Findings"/> not being so, <see cref="BackupFailure"/>
/// carries the reason the backup could not be created, so callers can show why the
/// clean was refused instead of a generic "no backup" message.</summary>
public sealed record RegistryReview(IReadOnlyList<RegistryFinding> Findings, IReadOnlyList<RegistryBackup> Backups, string? BackupFailure = null);

/// <summary>Outcome of cleaning selected registry findings.</summary>
public sealed record RegistryCleanResult(
    int Removed,
    IReadOnlyList<CleanupIssue> Skipped,
    IReadOnlyList<RegistryFinding>? Cleaned = null);

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
        @"Control Panel\Desktop\MuiCached",
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"AppEvents\Schemes\Apps\",
        @"Software\Microsoft\Windows\CurrentVersion\App Paths\",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\",
        ShellMuiCacheRoot + @"\"
    ];

    internal static bool IsDeletablePath(string? path, bool allowNamedClassesValue = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 500) return false;
        if (path.Contains('"') || path.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var root in AllowedCleanupRoots)
        {
            if (root.EndsWith('\\'))
            {
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // File-association scans only ever produce the per-user extension key
        // itself (for example Software\\Classes\\.txt). Do not let the broad
        // Software\\Classes\\ namespace become a general-purpose delete API.
        if (path.StartsWith(ClassesRoot + @"\.", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = path[(ClassesRoot.Length + 2)..];
            return !suffix.Contains('\\');
        }

        // File Extensions may also report a value on a ProgID key.  A named-value
        // deletion is safe here because it cannot remove the key or its children;
        // keep this opt-in so the broad Classes namespace is not a key-deletion
        // allow-list.
        if (allowNamedClassesValue
            && path.StartsWith(ClassesRoot + @"\", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = path[(ClassesRoot.Length + 1)..];
            return suffix.Length > 0 && !suffix.Contains('\\');
        }

        return false;
    }

    /// <summary>Whether a finding passes the safety gate for actual deletion.</summary>
    public static bool IsCleanable(RegistryFinding finding)
        => finding.LowRisk && finding.Confidence >= 70
           && finding.Hive == "HKCU"
           && IsDeletablePath(
               finding.Path,
               finding.ValueName is not null
                   && !string.IsNullOrWhiteSpace(finding.ValueName)
                   && finding.Category.Equals("File Extensions", StringComparison.OrdinalIgnoreCase));

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
        string? backupFailure = null;
        try
        {
            backups = await CreateBackupsAsync(safe, token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Best-effort: if we cannot back up (fresh profile, no permissions,
            // reg.exe unavailable), surface an empty review so the caller can
            // refuse to clean without a restore point - but keep the reason so
            // the refusal is diagnosable instead of a silent generic message.
            backups = [];
            backupFailure = ex.Message;
        }
        return new RegistryReview(safe, backups, backupFailure);
    }

    /// <summary>Deletes the key each cleanable finding points at. Every deletion is
    /// gated by IsCleanable and requires the key to still exist; anything else is
    /// reported in Skipped rather than acted on.</summary>
    public async Task<RegistryCleanResult> CleanAsync(
        RegistryReview review,
        CancellationToken token = default,
        IProgress<CleanupProgress>? progress = null)
    {
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
        var removed = 0;
        var skipped = new List<CleanupIssue>();
        var cleaned = new List<RegistryFinding>();
        if (review.Findings.Count > 0)
        {
            var verifiedBackup = false;
            foreach (var backup in review.Backups)
            {
                if (await ValidateBackupAsync(backup, token))
                {
                    verifiedBackup = true;
                    break;
                }
            }

            if (!verifiedBackup)
            {
                return new RegistryCleanResult(
                    0,
                    review.Findings.Select(f => new CleanupIssue(
                        f.Path, "Registry cleanup requires a verified backup.")).ToArray(),
                    []);
            }
        }

        // Registry edits are disk-bound work the page awaits on the UI thread.
        return await Task.Run(() =>
        {
            for (var index = 0; index < review.Findings.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var finding = review.Findings[index];
                progress?.Report(new CleanupProgress("Registry values", index + 1, review.Findings.Count, 0));
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
                        cleaned.Add(finding);
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
                        cleaned.Add(finding);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
                {
                    skipped.Add(new(finding.Path, ex.Message));
                }
            }
            return new RegistryCleanResult(removed, skipped, cleaned);
        }, token);
        }
        finally
        {
            CleanupCoordinator.Gate.Release();
        }
    }

    private static string ParentKeyPath(string path) => path[..path.LastIndexOf('\\')];
    private static string LeafKeyName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>The directory registry backups are written to. Under the packaged
    /// (MSIX) build, GetFolderPath resolves to the package's virtualized
    /// LocalCache location, so every reader and writer must go through this
    /// property instead of guessing at %LOCALAPPDATA%.</summary>
    public static string BackupsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanMachine", "Backups");

    /// <summary>How many .reg backup files exist in the backups directory
    /// (0 when it does not exist or cannot be read).</summary>
    public static int CountBackups() => CountBackups(BackupsDirectory);

    internal static int CountBackups(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.reg").Length
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Lists the .reg restore-point files in the backups directory,
    /// newest first. Empty (never throws) when the directory is missing or
    /// cannot be read. CreatedAt comes from the file's last-write time, which
    /// matches the stamp in its name.</summary>
    public static IReadOnlyList<RegistryBackup> ListBackups() => ListBackups(BackupsDirectory);

    internal static IReadOnlyList<RegistryBackup> ListBackups(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.reg")
                .Select(p => new RegistryBackup(p, new DateTimeOffset(File.GetLastWriteTimeUtc(p))))
                .OrderByDescending(b => b.CreatedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Permanently deletes one restore-point file. A missing file is
    /// not an error (idempotent); genuine failures throw for the caller to
    /// surface.</summary>
    public static void DeleteBackup(RegistryBackup backup)
    {
        if (File.Exists(backup.FilePath))
            File.Delete(backup.FilePath);
    }

    /// <summary>Exports the registry scopes the given findings will be deleted from:
    /// one whole-root export for Uninstall findings, one per-key export for
    /// Software\\Classes findings (that root is far too large to export whole).</summary>
    private static async Task<List<RegistryBackup>> CreateBackupsAsync(
        IReadOnlyList<RegistryFinding> findings, CancellationToken token)
    {
        var backups = new List<RegistryBackup>();
        var directory = BackupsDirectory;
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (findings.Any(f => f.Path.StartsWith(UninstallRoot + @"\", StringComparison.OrdinalIgnoreCase)))
            backups.Add(await ExportKeyWithRetryAsync(UninstallRoot,
                Path.Combine(directory, $"registry-uninstall-{stamp}.reg"), token));

        foreach (var path in findings
                     .Select(f => f.Path)
                     .Where(p => p.StartsWith(ClassesRoot + @"\", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = new string(path[(ClassesRoot.Length + 1)..]
                .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            backups.Add(await ExportKeyWithRetryAsync(path,
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
            backups.Add(await ExportKeyWithRetryAsync(path,
                Path.Combine(directory, $"registry-{name}-{stamp}.reg"), token));
        }
        if (backups.Count == 0)
            throw new InvalidOperationException("None of the selected findings are in a registry scope this tool backs up.");
        return backups;
    }

    // A single reg.exe export occasionally fails transiently (a momentary clash
    // with a live process, antivirus scanning the new file); the backup is the
    // restore point the whole clean depends on, so one flaky attempt must not
    // block it. One retry after a short delay covers that without turning a real
    // failure (missing key, access denied) into a long stall.
    internal static readonly TimeSpan ExportRetryDelay = TimeSpan.FromSeconds(1);
    internal const int ExportAttempts = 2;

    /// <summary>Exports a key with one bounded retry: the first attempt runs
    /// immediately, a failure waits <see cref="ExportRetryDelay"/> and tries once
    /// more. Cancellation is never retried. <paramref name="exportOnce"/> and
    /// <paramref name="delayAsync"/> are overridable so tests can exercise the
    /// policy without spawning reg.exe.</summary>
    internal static async Task<RegistryBackup> ExportKeyWithRetryAsync(
        string keyPath,
        string filePath,
        CancellationToken token,
        int attempts = ExportAttempts,
        Func<string, string, Task<RegistryBackup>>? exportOnce = null,
        Func<Task>? delayAsync = null)
    {
        exportOnce ??= (key, file) => ExportKeyAsync(key, file, token);
        delayAsync ??= () => Task.Delay(ExportRetryDelay, token);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await exportOnce(keyPath, filePath);
            }
            catch (InvalidOperationException) when (attempt < attempts)
            {
                await delayAsync();
            }
        }
    }

    private static async Task<RegistryBackup> ExportKeyAsync(string keyPath, string filePath, CancellationToken token)
    {
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("export");
        psi.ArgumentList.Add($"HKCU\\{keyPath}");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("/y");
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry export tool.");
        // Read stderr fully before waiting: reg.exe writes its errors there, and a
        // detailed message (access denied, disk error, bad key) beats a generic one.
        var stderr = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0 || !File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            throw new InvalidOperationException(
                $"Registry backup export failed (exit code {process.ExitCode}"
                + (string.IsNullOrWhiteSpace(stderr) ? ")." : $": {stderr.Trim()})."));

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

        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(backup.FilePath);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry restore tool.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Registry backup restore failed.");
    }
}
