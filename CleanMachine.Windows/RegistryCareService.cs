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

    /// <summary>Whether a recorded restore point covers a registry root this app is
    /// allowed to touch at all.
    /// <para>
    /// Restore and delete do not deserve different rules, and until provenance
    /// existed they got them: deletion is gated by an allow-list that is
    /// re-checked at the moment of the write, while restore handed any
    /// <c>.reg</c> file in a backups folder straight to <c>reg import</c>, which
    /// applies every key and value in the file. A restore can therefore write
    /// exactly what the cleaner refuses to delete, so it is bounded by the same
    /// roots.
    /// </para>
    /// <para>
    /// It is deliberately a little wider than <see cref="IsDeletablePath"/>, in two
    /// respects that follow from what a restore actually is. The whole Uninstall
    /// root is exported as one restore point, so the root itself has to be an
    /// acceptable scope even though it is never an acceptable deletion target;
    /// and a restore writes values, not keys, so anything beneath an allowed root
    /// is within the area the cleaner already operates in.
    /// </para>
    /// </summary>
    internal static bool IsRestorableScope(string? keyRoot)
    {
        if (string.IsNullOrWhiteSpace(keyRoot) || keyRoot.Length > 500) return false;
        if (keyRoot.Contains('"')) return false;

        foreach (var root in AllowedCleanupRoots)
        {
            var trimmed = root.TrimEnd('\\');
            if (keyRoot.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            if (keyRoot.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Mirrors the per-user extension rule in IsDeletablePath: the per-user
        // <c>Software\\Classes\\.&lt;ext&gt;</c> key and nothing below it.
        if (keyRoot.StartsWith(ClassesRoot + @"\.", StringComparison.OrdinalIgnoreCase))
            return !keyRoot[(ClassesRoot.Length + 2)..].Contains('\\');

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
        using var cleaning = CleaningActivity.Begin();
        await CleanupCoordinator.Gate.WaitAsync(token);
        try
        {
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
                var removed = 0;
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

    /// <summary>The preferred directory registry backups are written to. Every
    /// reader and writer must go through <see cref="BackupDirectories"/> rather
    /// than guessing at %LOCALAPPDATA%; this is simply its first entry. On a
    /// packaged install the app's own writes here are redirected into the
    /// package folder, so this directory is not always usable - see
    /// <see cref="BackupDirectories"/>.</summary>
    public static string BackupsDirectory => Path.Combine(
        AppDataPaths.Root, "Backups");

    /// <summary>Every directory a restore point may live in, most preferred
    /// first, without duplicates.
    /// <para>Index 0 is <see cref="BackupsDirectory"/>, which is correct for
    /// unpackaged builds and for any machine where this process and the tools it
    /// spawns agree on where %LOCALAPPDATA% really is. The later entries exist
    /// because on a packaged (MSIX) install they demonstrably do not: the app's
    /// own writes under %LOCALAPPDATA% are redirected into the package folder,
    /// while reg.exe - a child process outside the package - writes to the
    /// literal path, which therefore does not physically exist and reg.exe fails
    /// with "Unable to write to the file". The user profile root sits outside
    /// every redirected known folder, so the app and reg.exe can both write
    /// there; %TEMP% is the last resort.</para>
    /// <para>The export walks this list and keeps the first directory reg.exe
    /// actually succeeds in, and the Backups page reads all of them, so a
    /// restore point is never invisible just because the machine forced it
    /// somewhere unexpected.</para></summary>
    internal static IReadOnlyList<string> BackupDirectories
    {
        get
        {
            var directories = new List<string> { BackupsDirectory };
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                directories.Add(Path.Combine(profile, AppDataPaths.FolderName, "Backups"));
            var temp = Path.GetTempPath();
            if (!string.IsNullOrWhiteSpace(temp))
                directories.Add(Path.Combine(temp, AppDataPaths.FolderName, "Backups"));
            return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>How many .reg backup files exist across every backup directory
    /// (0 when none exists or cannot be read).</summary>
    public static int CountBackups() => BackupDirectories.Sum(CountBackups);

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

    /// <summary>Lists the .reg restore-point files across every backup directory,
    /// newest first. Empty (never throws) when they are missing or cannot be
    /// read. CreatedAt comes from the file's last-write time, which matches the
    /// stamp in its name.</summary>
    public static IReadOnlyList<RegistryBackup> ListBackups()
    {
        // The provenance record is read once: matching it hashes every candidate
        // file, and this walks up to three directories.
        var provenance = BackupProvenance.Load();
        return BackupDirectories
            .SelectMany(directory => ListBackups(directory, provenance))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    internal static IReadOnlyList<RegistryBackup> ListBackups(string directory)
        => ListBackups(directory, []);

    /// <summary>Lists the <c>.reg</c> files in one backup directory, marking which of
    /// them this app can prove it wrote.
    /// <para>
    /// An unprovenanced file is still listed: the user should be able to see that
    /// it exists and delete it. It simply carries no key scope, is not restorable,
    /// and must not be given a friendly "this is your Uninstall entries backup"
    /// label derived from its name - the name is the one thing about a planted
    /// file its writer fully controls.</para>
    /// </summary>
    internal static IReadOnlyList<RegistryBackup> ListBackups(
        string directory, IReadOnlyList<BackupProvenanceEntry> provenance)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.reg")
                .Select(p =>
                {
                    var match = BackupProvenance.Match(p, provenance);
                    return new RegistryBackup(
                        p,
                        match?.CreatedAtUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(p)),
                        match?.KeyRoot ?? string.Empty,
                        match is not null);
                })
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
        // The preferred directory. Each candidate is created on demand inside
        // TryExportAsync, because a directory the app can create is not
        // necessarily one reg.exe can write to.
        var directory = BackupsDirectory;
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
    // failure (missing key, access denied) into a long stall. Each attempt walks
    // every <see cref="BackupDirectories"/> candidate, so the worst case is a
    // handful of short-lived processes before the clean is refused.
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
        var fileName = Path.GetFileName(filePath);
        var failures = new List<string>();
        foreach (var directory in BackupDirectories)
        {
            var target = Path.Combine(directory, fileName);
            var (backup, failure) = await TryExportAsync(keyPath, target, token);
            if (backup is null)
            {
                failures.Add($"{target}: {failure}");
                continue;
            }

            // Record what reg.exe actually wrote, in whichever directory it managed
            // to write it, before anything else can get at the bytes. Without this
            // the file is just a .reg in a folder, which is not evidence of anything.
            BackupProvenance.Record(target, keyPath, backup.CreatedAt);

            // Prefer the app's own folder when it can also hold the file, so the
            // Backups page has one obvious place to look.
            if (!string.Equals(target, filePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(target, filePath, overwrite: true);
                    BackupProvenance.Record(filePath, keyPath, backup.CreatedAt);
                    return backup with { FilePath = filePath, KeyRoot = keyPath, Verified = true };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The restore point is valid where it is; losing it because the
                    // app's own folder is unwritable would be worse than the tidier
                    // layout, and ListBackups reads every directory anyway.
                }
            }
            return backup with { KeyRoot = keyPath, Verified = true };
        }

        throw new InvalidOperationException(
            $"Registry backup export failed for HKCU\\{keyPath}"
            + $" ({string.Join("; ", failures)}).");
    }

    private static async Task<(RegistryBackup? Backup, string? Failure)> TryExportAsync(
        string keyPath, string filePath, CancellationToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            EnsureExportTargetUsable(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException
                                       or InvalidOperationException)
        {
            return (null, ex.Message);
        }

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
            return (null, $"exit code {process.ExitCode}"
                + (string.IsNullOrWhiteSpace(stderr) ? $", no output file at {filePath}" : $": {stderr.Trim()}"));

        return (new RegistryBackup(filePath, DateTimeOffset.UtcNow), null);
    }

    /// <summary>Proves the export destination is creatable and writable <i>before</i>
    /// reg.exe runs, then removes the placeholder.
    /// <para>CreateDirectory succeeding does not prove reg.exe can write there -
    /// a redirected write lands somewhere else entirely - so the destination is
    /// touched directly. This separates "the app cannot write here" from "only
    /// reg.exe cannot", which is the difference between a fixable path and a
    /// dead end. The placeholder is removed so no empty .reg file is left for
    /// ValidateBackupAsync to later mistake for a restore point.</para></summary>
    internal static void EnsureExportTargetUsable(string filePath)
    {
        try
        {
            using (File.Create(filePath)) { }
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"The registry backup folder '{Path.GetDirectoryName(filePath)}' is not writable ({ex.Message}).");
        }
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

        // Provenance gate. A .reg file is a script, and the backup directories
        // include %TEMP%, so a file that merely looks like a restore point is
        // exactly the thing an attacker would plant. Only bytes this app exported,
        // still hashing to what it recorded, may be imported.
        var match = BackupProvenance.Match(backup.FilePath, BackupProvenance.Load())
            ?? throw new InvalidDataException(
                "This file is not a restore point CleanMachine created: it is not in the app's own "
                + "record of what it exported, or its contents have changed since. Nothing was imported. "
                + "Restore points written by an older version of the app, or copied in by hand, are no "
                + "longer restorable from here - open the file with regedit directly if you trust it.");

        // Scope gate: the recorded key root must be one the cleaner is allowed to
        // touch at all. Restore writes whatever the file contains, so it gets the
        // same allow-list as deletion rather than a wider one.
        if (!IsRestorableScope(match.KeyRoot))
            throw new InvalidDataException(
                $"This restore point covers HKCU\\{match.KeyRoot}, which is outside the registry areas "
                + "CleanMachine backs up. Nothing was imported.");

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
