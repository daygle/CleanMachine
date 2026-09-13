using System.Diagnostics;
using System.Security.Cryptography;

namespace CleanMachine.Windows;

/// <summary>A fixed drive that free-space wiping can target.</summary>
public sealed record DriveWipeTarget(string RootPath, string Label, long TotalBytes, long FreeBytes, bool IsSystemDrive)
{
    public string DisplayName => string.IsNullOrEmpty(Label) ? RootPath : $"{Label} ({RootPath})";
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
                        string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase)));
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
        }
    }

    /// <summary>Wipes the drive's free space with the requested number of passes.
    /// The last pass is a zero fill so the freed clusters hold only zeros.
    /// Progress is reported per pass as bytes written.</summary>
    public async Task<DriveWipeResult> WipeFreeSpaceAsync(
        DriveWipeTarget target,
        int passes,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken token = default)
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

        watch.Stop();
        return new DriveWipeResult(clampedPasses, totalWritten, watch.Elapsed);
    }
}
