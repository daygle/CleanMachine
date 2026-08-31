namespace CleanMachine.Windows;

public enum CleanupRisk { Safe, Review, Advanced }
public sealed record WindowsCleanupFinding(string Id, string Name, string Description, string Location, long Bytes, CleanupRisk Risk, bool Selected);

public sealed class WindowsCleanupService
{
    private static readonly string[] SafeExtensions = [".tmp", ".dmp", ".log"];
    private const int MaxFiles = 10_000;

    public Task<IReadOnlyList<WindowsCleanupFinding>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wer = Path.Combine(local, "Microsoft", "Windows", "WER");
        var explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        return Task.FromResult<IReadOnlyList<WindowsCleanupFinding>>([
            CreateFinding("user-temp", "User temporary files", "Old temporary files no longer in use", temp, GetSafeSize(temp), true),
            CreateFinding("thumbnail-cache", "Thumbnail cache", "Cached image previews Windows can recreate", explorer, GetFileSize(explorer, "thumbcache*.db"), true),
            CreateFinding("error-reports", "Error reports", "Old application crash reports and diagnostics", wer, GetDirectorySize(wer), true),
            new("recycle-bin", "Recycle Bin", "Deleted items awaiting permanent removal", "All local drives", 0, CleanupRisk.Review, false),
            new("old-updates", "Windows Update leftovers", "Stale update downloads after installation", "Windows Update cache", 0, CleanupRisk.Advanced, false)
        ]);
    }

    public async Task<CleanupReport> CleanSelectedAsync(IEnumerable<WindowsCleanupFinding> findings, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = findings.Where(f => f.Selected && f.Risk == CleanupRisk.Safe && Directory.Exists(f.Location) && NativeSafety.IsSafeFileCandidate(f.Location)).ToArray();
        var files = selected.SelectMany(f => EnumerateFiles(f.Location, f.Id == "thumbnail-cache" ? "thumbcache*.db" : "*")).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToArray();
        var skipped = new List<CleanupIssue>(); var removed = 0; long recovered = 0;
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            try
            {
                if (!NativeSafety.IsSafeFileCandidate(file) || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-2)) { skipped.Add(new(file, "Protected, reparse-point, or recently modified")); continue; }
                var info = new FileInfo(file); var length = info.Length; File.Delete(file); removed++; recovered += length;
            }
            catch (IOException) { skipped.Add(new(file, "Locked or unavailable")); }
            catch (UnauthorizedAccessException) { skipped.Add(new(file, "Access denied")); }
            progress?.Report(new CleanupProgress("Windows cleanup", index + 1, files.Length, recovered));
        }
        return new CleanupReport(new CleanupResult(removed, recovered), skipped);
    }

    public Task<CleanupResult> CleanSelectedAsync(IEnumerable<WindowsCleanupFinding> findings, CancellationToken cancellationToken = default) => CleanSelectedAsync(findings, null, cancellationToken).ContinueWith(t => t.Result.Result, cancellationToken);

    private static WindowsCleanupFinding CreateFinding(string id, string name, string description, string location, long bytes, bool selected) => new(id, name, description, location, bytes, CleanupRisk.Safe, selected);
    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory) || NativeSafety.IsProtectedPath(directory)) return [];
        try { return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(f => NativeSafety.IsSafeFileCandidate(f, directory)).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    private static long GetSafeSize(string directory) => EnumerateFiles(directory, "*").Where(f => SafeExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Sum(GetLength);
    private static long GetFileSize(string directory, string pattern) => EnumerateFiles(directory, pattern).Sum(GetLength);
    private static long GetDirectorySize(string directory) => EnumerateFiles(directory, "*").Sum(GetLength);
    private static long GetLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
}
