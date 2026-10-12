using System.Buffers.Binary;

namespace CleanMachine.Windows;

/// <summary>Removes Recycle Bin items older than a given age. It only ever touches
/// files that are already in the Recycle Bin (i.e. already deleted by the user):
/// on each fixed drive it reads the per-item metadata files ($I...) under
/// <c>$Recycle.Bin</c>, and for those whose deletion is older than the cutoff it
/// deletes the metadata and its matching data ($R...) entry. Only the current
/// user's SID folder is considered; unreadable or unknown metadata is skipped.</summary>
public static class RecycleBinService
{
    public static (int Removed, long Bytes) EmptyOlderThan(int days, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) return (0, 0);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        var removed = 0;
        long bytes = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(bin)) continue;

                foreach (var sidDir in SafeEnumerate(() => Directory.EnumerateDirectories(bin, sid)))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var meta in SafeEnumerate(() => Directory.EnumerateFiles(sidDir, "$I*")))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            // The $I header records when the item was actually sent
                            // to the bin. Creation time is not a deletion date:
                            // malformed or unknown metadata must be left alone.
                            if (NativeSafety.IsReparsePoint(meta)) continue;
                            var deletedAt = ReadDeletionTimeUtc(meta);
                            // An unknown age must never become permission to delete.
                            if (deletedAt is null || deletedAt > cutoff) continue;

                            // Matching data entry: "$I<rest>" -> "$R<rest>".
                            var data = Path.Combine(sidDir, "$R" + Path.GetFileName(meta)[2..]);
                            // Every other deletion path in the app proves the target
                            // is a real, non-link path before removing it. The Recycle
                            // Bin was the one place that skipped it, which left the
                            // guarantee resting on the recursive-delete implementation
                            // not descending into links rather than on a check the app
                            // makes.
                            //
                            // Checked only on the branches where the entry actually
                            // exists: IsReparsePoint fails closed, so testing a missing
                            // path would return true and strand every orphaned $I file
                            // instead of clearing it, as it did before.
                            if (!NativeSafety.IsWithin(data, sidDir)) continue;

                            long size = 0;
                            if (File.Exists(data))
                            {
                                if (NativeSafety.IsReparsePoint(data)) continue;
                                size = SafeDeletion.DeleteFile(data, sidDir);
                                CleanupJournal.Record(1, size);
                            }
                            else if (Directory.Exists(data))
                            {
                                if (NativeSafety.IsReparsePoint(data)) continue;
                                size = SafeDeletion.DeleteTree(data, token);
                            }
                            SafeDeletion.DeleteFile(meta, sidDir);
                            if (size == 0) CleanupJournal.Record(1, 0);
                            removed++;
                            bytes += size;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return (removed, bytes);
    }

    /// <summary>The deletion time stored in a <c>$I</c> metadata file, or null when
    /// it cannot be read.
    /// <para>
    /// The file starts with a version, the original size, and the FILETIME at
    /// which the item was deleted. Both known layouts put the size and the
    /// timestamp at offset 16 for both version 1 and version 2 (the version
    /// field is eight bytes in both). Unknown versions fail closed.
    /// </para></summary>
    internal static DateTime? ReadDeletionTimeUtc(string metaPath)
    {
        try
        {
            using var stream = File.Open(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // stackalloc + BinaryPrimitives: the header is a fixed 24-byte little-endian
            // structure, and this runs across every Recycle Bin item, so there is no
            // reason to heap-allocate a byte[] per file just to feed BitConverter.
            Span<byte> header = stackalloc byte[24];
            if (stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24) return null;
            var version = BinaryPrimitives.ReadInt64LittleEndian(header);
            if (version is not (1 or 2)) return null;
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            if (fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try { return enumerate().ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

}
