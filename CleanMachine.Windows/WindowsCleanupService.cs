namespace CleanMachine.Windows;

public enum CleanupRisk { Safe, Review, Advanced }
public sealed record WindowsCleanupFinding(string Id, string Name, string Description, string Location, long Bytes, CleanupRisk Risk, bool Selected);

public sealed class WindowsCleanupService
{
    private static readonly string[] SafeExtensions = [".tmp", ".dmp", ".log"];
    public Task<IReadOnlyList<WindowsCleanupFinding>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var findings = new List<WindowsCleanupFinding>
        {
            new("user-temp", "User temporary files", "Old temporary files no longer in use", temp, GetSafeSize(temp), CleanupRisk.Safe, true),
            new("thumbnail-cache", "Thumbnail cache", "Cached image previews Windows can recreate", Path.Combine(local, "Microsoft", "Windows", "Explorer"), GetFileSize(Path.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache*.db"), CleanupRisk.Safe, true),
            new("error-reports", "Error reports", "Old application crash reports and diagnostics", Path.Combine(local, "Microsoft", WindowsErrorReportsPath()), GetDirectorySize(Path.Combine(local, "Microsoft", WindowsErrorReportsPath())), CleanupRisk.Safe, true),
            new("recycle-bin", "Recycle Bin", "Deleted items awaiting permanent removal", "All local drives", 0, CleanupRisk.Review, false),
            new("old-updates", "Windows Update leftovers", "Stale update downloads after installation", "Windows Update cache", 0, CleanupRisk.Advanced, false)
        };
        return Task.FromResult<IReadOnlyList<WindowsCleanupFinding>>(findings);
    }
    public Task<CleanupResult> CleanSelectedAsync(IEnumerable<WindowsCleanupFinding> findings, CancellationToken cancellationToken = default)
    {
        var selected = findings.Where(f => f.Selected && f.Risk == CleanupRisk.Safe).ToList(); var removed = 0; long recovered = 0;
        foreach (var finding in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pattern = finding.Id == "thumbnail-cache" ? "thumbcache*.db" : "*";
            foreach (var file in EnumerateFiles(finding.Location, pattern))
            {
                try { var size = new FileInfo(file).Length; File.Delete(file); recovered += size; removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return Task.FromResult(new CleanupResult(removed, recovered));
    }
    private static IEnumerable<string> EnumerateFiles(string directory, string pattern) { try { return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Where(IsSafeFile).ToArray() : []; } catch { return []; } }
    private static bool IsSafeFile(string file) { var info = new FileInfo(file); return info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-2) && (SafeExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase) || info.Name.StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase)); }
    private static long GetSafeSize(string directory) => EnumerateFiles(directory, "*").Where(f => SafeExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Sum(f => new FileInfo(f).Length);
    private static long GetFileSize(string directory, string pattern) => EnumerateFiles(directory, pattern).Sum(f => new FileInfo(f).Length);
    private static long GetDirectorySize(string directory) => EnumerateFiles(directory, "*").Sum(f => new FileInfo(f).Length);
    private static string WindowsErrorReportsPath() => @"Windows\WER";
}
