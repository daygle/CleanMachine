namespace CleanMachine.Windows;

public enum CleanupRisk { Safe, Review, Advanced }

public sealed record WindowsCleanupFinding(
    string Id,
    string Name,
    string Description,
    string Location,
    long Bytes,
    CleanupRisk Risk,
    bool Selected);

public sealed class WindowsCleanupService
{
    private static readonly string[] SafeExtensions = [".tmp", ".dmp", ".log"];

    public Task<IReadOnlyList<WindowsCleanupFinding>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        var findings = new List<WindowsCleanupFinding>
        {
            new("user-temp", "User temporary files", "Temporary files no longer in use", temp, GetDirectorySize(temp), CleanupRisk.Safe, true),
            new("thumbnail-cache", "Thumbnail cache", "Cached image previews Windows can recreate", "User profile cache", 0, CleanupRisk.Safe, true),
            new("error-reports", "Error reports", "Old application crash reports and diagnostics", "User profile diagnostics", 0, CleanupRisk.Safe, true),
            new("recycle-bin", "Recycle Bin", "Deleted items awaiting permanent removal", "All local drives", 0, CleanupRisk.Review, false),
            new("old-updates", "Windows Update leftovers", "Stale update downloads after installation", "Windows Update cache", 0, CleanupRisk.Advanced, false),
        };
        return Task.FromResult<IReadOnlyList<WindowsCleanupFinding>>(findings);
    }

    public Task<CleanupResult> CleanSelectedAsync(IEnumerable<WindowsCleanupFinding> findings, CancellationToken cancellationToken = default)
    {
        var selected = findings.Where(f => f.Selected && f.Risk != CleanupRisk.Advanced).ToList();
        long recovered = 0;
        var removed = 0;
        foreach (var finding in selected)
        {
            if (finding.Id == "user-temp")
            {
                var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
                foreach (var file in EnumerateSafeFiles(temp))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { var length = new FileInfo(file).Length; File.Delete(file); recovered += length; removed++; } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        return Task.FromResult(new CleanupResult(removed, recovered));
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly); } catch { yield break; }
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (SafeExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase) && info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-2)) yield return file;
        }
    }

    private static long GetDirectorySize(string directory)
    {
        try { return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Select(file => new FileInfo(file)).Where(info => SafeExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase) && info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-2)).Sum(info => info.Length); } catch { return 0; }
    }
}
