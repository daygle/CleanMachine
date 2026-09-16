using System.Diagnostics;
using System.Security.Cryptography;

namespace CleanMachine.Windows;

/// <summary>A fixed drive that free-space wiping can target.</summary>
public sealed record DriveWipeTarget(string RootPath, string Label, long TotalBytes, long FreeBytes, bool IsSystemDrive, string FileSystem = "")
{
    public string DisplayName => string.IsNullOrEmpty(Label) ? RootPath : $"{Label} ({RootPath})";

    /// <summary>NTFS keeps small deleted files resident in the Master File Table, so
    /// wiping MFT free space applies only to NTFS volumes.</summary>
    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>FAT/exFAT volumes have no MFT; the analogous metadata is freed
    /// directory entries.</summary>
    public bool IsFat => FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
        || FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
}

public sealed record DriveWipeResult(int Passes, long BytesOverwritten, TimeSpan Duration);

/// <summary>Drive wiper: overwrites the free (unused) space of a fixed drive so that
/// previously deleted files become unrecoverable, then deletes the temporary
/// wiper file so the space is free again. Existing files are never touched - the
/// wiper only writes to space that is already unused.</summary>
public sealed class DriveWiperService
{
    private const string WiperFileName = "CleanMachine.free-wipe.tmp";
    // Always leave this much (or 1%, whichever is larger) free so Windows keeps
    // breathing during and after a wipe.
    private const long ReserveBytes = 64L * 1024 * 1024;

    /// <summary>Picks the wiper file location for a drive. Standard users cannot
    /// usually write to a drive's root (C:\ requires admin), so Users\Public is
    /// preferred when present; free clusters are volume-wide, so wiping through a
    /// file anywhere on the volume is equally effective.</summary>
    private static string WiperPathFor(DriveWipeTarget target)
    {
        var publicDir = Path.Combine(target.RootPath, "Users", "Public");
        if (Directory.Exists(publicDir)) return Path.Combine(publicDir, WiperFileName);
        return Path.Combine(target.RootPath, WiperFileName);
    }

    /// <summary>Lists internal fixed drives suitable for wiping. Removable,
    /// network, and optical drives are excluded.</summary>
    public static IReadOnlyList<DriveWipeTarget> ListDrives()
    {
        var result = new List<DriveWipeTarget>();
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var root = Path.GetPathRoot(drive.Name) ?? drive.Name;
                    result.Add(new DriveWipeTarget(
                        root,
                        drive.VolumeLabel,
                        drive.TotalSize,
                        drive.AvailableFreeSpace,
                        string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase),
                        drive.DriveFormat));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        return result;
    }

    /// <summary>Removes wiper temp files left behind by an interrupted run.</summary>
    public static void CleanupAbandonedWiperFiles()
    {
        foreach (var drive in ListDrives())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(drive.RootPath, WiperFileName),
                Path.Combine(drive.RootPath, "Users", "Public", WiperFileName)
            })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // Also sweep any metadata-churn folder left by an interrupted run.
            foreach (var metaDir in new[]
            {
                Path.Combine(drive.RootPath, "CleanMachine.meta-wipe"),
                Path.Combine(drive.RootPath, "Users", "Public", "CleanMachine.meta-wipe")
            })
            {
                try { if (Directory.Exists(metaDir)) Directory.Delete(metaDir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Wipes the drive's free space with the requested number of passes.
    /// The last pass is a zero fill so the freed clusters hold only zeros.
    /// Progress is reported per pass as bytes written.</summary>
    public async Task<DriveWipeResult> WipeFreeSpaceAsync(
        DriveWipeTarget target,
        int passes,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken token = default,
        bool wipeMftFreeSpace = false,
        bool wipeFatFreeSpace = false)
    {
        var clampedPasses = Math.Clamp(passes, 1, 8);
        var wiperPath = WiperPathFor(target);
        // 1 MiB chunks keep memory tiny while avoiding per-call overhead.
        var buffer = new byte[1024 * 1024];
        var watch = Stopwatch.StartNew();
        long totalWritten = 0;

        try
        {
            // Writable free space right now; keep the reserve untouched.
            var free = new DriveInfo(target.RootPath).AvailableFreeSpace;
            var writable = free - Math.Max(ReserveBytes, free / 100);
            if (writable < 16L * 1024 * 1024)
                throw new InvalidOperationException(
                    $"The drive has only {AppNotifications.FormatBytes(free)} free; at least 80 MB must stay available to run a safe wipe.");

            await using (var stream = new FileStream(wiperPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.WriteThrough))
            {
                stream.SetLength(writable);
                for (var pass = 0; pass < clampedPasses; pass++)
                {
                    token.ThrowIfCancellationRequested();
                    var isFinalPass = pass == clampedPasses - 1;
                    if (isFinalPass)
                        Array.Clear(buffer, 0, buffer.Length); // freed clusters end as zeros
                    else
                        RandomNumberGenerator.Fill(buffer);    // random data defeats recovery

                    stream.Seek(0, SeekOrigin.Begin);
                    long written = 0;
                    while (written < writable)
                    {
                        token.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(buffer.Length, writable - written);
                        await stream.WriteAsync(buffer.AsMemory(0, count), token);
                        written += count;
                        progress?.Report(new CleanupProgress(
                            $"Pass {pass + 1} of {clampedPasses}",
                            (int)(written >> 20),
                            (int)(writable >> 20),
                            written));
                    }
                    await stream.FlushAsync(token);
                    totalWritten += written;
                }
            }
        }
        finally
        {
            // Whether the wipe finished, was cancelled, or failed: the wiper file
            // must go so the space is free again.
            try { if (File.Exists(wiperPath)) File.Delete(wiperPath); }
            catch { /* best-effort; CleanupAbandonedWiperFiles also sweeps */ }
        }

        // The big-file pass above overwrites free clusters, but not the filesystem's
        // own free metadata (NTFS MFT records that held small resident files, or FAT
        // directory entries of deleted files). Overwrite that separately when asked
        // and applicable to this volume's filesystem.
        if (wipeMftFreeSpace && target.IsNtfs)
            totalWritten += await WipeMetadataFreeSpaceAsync(target, "Wiping MFT free space", progress, token);
        if (wipeFatFreeSpace && target.IsFat)
            totalWritten += await WipeMetadataFreeSpaceAsync(target, "Wiping FAT free space", progress, token);

        watch.Stop();
        return new DriveWipeResult(clampedPasses, totalWritten, watch.Elapsed);
    }

    // Where the metadata-churn temp files live (a sub-folder next to the wiper file).
    private static string MetaWipeDirFor(DriveWipeTarget target)
        => Path.Combine(Path.GetDirectoryName(WiperPathFor(target))!, "CleanMachine.meta-wipe");

    // Bounded so the churn always terminates even on a volume with vast free space.
    private const int MaxMetadataFiles = 100_000;

    /// <summary>Overwrites the volume's free filesystem metadata - the free records and
    /// slack of the NTFS Master File Table (small deleted files can leave data resident
    /// there), or the freed directory entries of a FAT/exFAT volume - which a free-cluster
    /// wipe does not reach. It creates many small files (each just large enough to be
    /// MFT-resident on NTFS) so the freed metadata is rewritten, then deletes them.
    /// Existing files are never touched; a reserve of free space is always kept.
    /// Best-effort and bounded: it stops at the reserve or a file-count cap.</summary>
    private static async Task<long> WipeMetadataFreeSpaceAsync(
        DriveWipeTarget target, string phase, IProgress<CleanupProgress>? progress, CancellationToken token)
    {
        var dir = MetaWipeDirFor(target);
        Directory.CreateDirectory(dir);
        var buffer = new byte[512];
        long created = 0;
        long bytes = 0;
        try
        {
            for (var i = 0; i < MaxMetadataFiles; i++)
            {
                token.ThrowIfCancellationRequested();
                // Check the reserve only periodically - AvailableFreeSpace is a syscall.
                if ((i & 0xFF) == 0)
                {
                    var free = new DriveInfo(target.RootPath).AvailableFreeSpace;
                    if (free <= Math.Max(ReserveBytes, free / 100)) break;
                }
                RandomNumberGenerator.Fill(buffer);
                try
                {
                    await using var s = new FileStream(
                        Path.Combine(dir, $"m{i:D7}.tmp"),
                        FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.WriteThrough);
                    await s.WriteAsync(buffer, token);
                }
                catch (IOException) { break; }               // out of records/space - done
                catch (UnauthorizedAccessException) { break; }
                created++;
                bytes += buffer.Length;
                if ((created & 0x3FF) == 0)
                    progress?.Report(new CleanupProgress(phase, (int)(created / 1000), MaxMetadataFiles / 1000, bytes));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort; CleanupAbandonedWiperFiles also sweeps */ }
        }
        return bytes;
    }
}
