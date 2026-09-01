using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

public enum CleanupRisk { Safe, Review, Advanced }
public sealed record WindowsCleanupFinding(string Id, string Name, string Description, string Location, long Bytes, CleanupRisk Risk, bool Selected);
public sealed record WindowsCleanupOptions(bool ConfirmReviewCategories = false, bool AllowElevation = false, IReadOnlySet<string>? ExcludedPaths = null);

public sealed class WindowsCleanupService
{
    private static readonly string[] SafeExtensions = [".tmp", ".dmp", ".log"];
    private const int MaxFiles = 10_000;
    private const string RecycleBinId = "recycle-bin";
    private const string WindowsUpdateId = "old-updates";

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
            new(RecycleBinId, "Recycle Bin", "Deleted items awaiting permanent removal", "All local drives", 0, CleanupRisk.Review, false),
            new(WindowsUpdateId, "Windows Update leftovers", "Stale update downloads after installation", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download"), 0, CleanupRisk.Advanced, false)
        ]);
    }

    public async Task<CleanupReport> CleanSelectedAsync(IEnumerable<WindowsCleanupFinding> findings, WindowsCleanupOptions options, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = findings.Where(f => f.Selected).ToArray();
        var issues = new List<CleanupIssue>(); var removed = 0; long recovered = 0;
        var fileFindings = selected.Where(f => f.Risk == CleanupRisk.Safe && Directory.Exists(f.Location) && NativeSafety.IsSafeFileCandidate(f.Location)).ToArray();
        var files = fileFindings.SelectMany(f => EnumerateFiles(f.Location, f.Id == "thumbnail-cache" ? "thumbcache*.db" : "*", options.ExcludedPaths)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var file = files[index];
            try
            {
                if (!NativeSafety.IsSafeFileCandidate(file) || IsExcluded(file, options.ExcludedPaths) || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-2)) { issues.Add(new(file, "Protected, excluded, reparse-point, or recently modified")); continue; }
                var info = new FileInfo(file); var length = info.Length; File.Delete(file); removed++; recovered += length;
            }
            catch (IOException) { issues.Add(new(file, "Locked or unavailable")); }
            catch (UnauthorizedAccessException) { issues.Add(new(file, "Access denied")); }
            progress?.Report(new CleanupProgress("Windows cleanup", index + 1, files.Length, recovered));
        }
        foreach (var finding in selected.Where(f => f.Risk != CleanupRisk.Safe))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!options.ConfirmReviewCategories) { issues.Add(new(finding.Location, "Explicit review confirmation required")); continue; }
            if (finding.Id == RecycleBinId) { try { EmptyRecycleBin(); removed++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { issues.Add(new(finding.Location, $"Recycle Bin cleanup failed: {ex.Message}")); } }
            else if (finding.Id == WindowsUpdateId) { if (!options.AllowElevation) issues.Add(new(finding.Location, "Administrator elevation is required")); else issues.Add(new(finding.Location, "Windows Update cleanup requires a Windows service/API implementation and remains disabled")); }
        }
        return new CleanupReport(new CleanupResult(removed, recovered), issues);
    }

    private static void EmptyRecycleBin()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycle Bin cleanup is supported on Windows only.");
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
        if (result != 0) throw new IOException($"Windows returned error code {result}.");
    }

    private static WindowsCleanupFinding CreateFinding(string id, string name, string description, string location, long bytes, bool selected) => new(id, name, description, location, bytes, CleanupRisk.Safe, selected);
    private static IEnumerable<string> EnumerateFiles(string directory, string pattern, IReadOnlySet<string>? exclusions)
    {
        if (!Directory.Exists(directory) || NativeSafety.IsProtectedPath(directory) || IsExcluded(directory, exclusions)) return [];
        try { return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(f => NativeSafety.IsSafeFileCandidate(f, directory) && !IsExcluded(f, exclusions)).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    private static bool IsExcluded(string path, IReadOnlySet<string>? exclusions) => exclusions?.Any(root => NativeSafety.IsWithin(path, root)) == true;
    private static long GetSafeSize(string directory) => EnumerateFiles(directory, "*", null).Where(f => SafeExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Sum(GetLength);
    private static long GetFileSize(string directory, string pattern) => EnumerateFiles(directory, pattern, null).Sum(GetLength);
    private static long GetDirectorySize(string directory) => EnumerateFiles(directory, "*", null).Sum(GetLength);
    private static long GetLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
