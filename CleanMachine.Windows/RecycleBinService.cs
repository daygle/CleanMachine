namespace CleanMachine.Windows;

/// <summary>Removes Recycle Bin items older than a given age. It only ever touches
/// files that are already in the Recycle Bin (i.e. already deleted by the user):
/// on each fixed drive it reads the per-item metadata files ($I...) under
/// <c>$Recycle.Bin</c>, and for those whose deletion is older than the cutoff it
/// deletes the metadata and its matching data ($R...) entry. Access-denied folders
/// (other users' SIDs, which need elevation) are skipped.</summary>
public static class RecycleBinService
{
    public static (int Removed, long Bytes) EmptyOlderThan(int days, CancellationToken token = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var removed = 0;
        long bytes = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (!Directory.Exists(bin)) continue;

                foreach (var sidDir in SafeEnumerate(() => Directory.EnumerateDirectories(bin)))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var meta in SafeEnumerate(() => Directory.EnumerateFiles(sidDir, "$I*")))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            // The $I header records when the item was actually sent
                            // to the bin. Prefer that over the file's creation time:
                            // a creation time is settable by anything that can write
                            // the file (and is simply wrong after a copy or a
                            // restore), so it made the cutoff a thing the writer of
                            // the file could decide. Falls back to the creation time
                            // only when the header cannot be read.
                            var deletedAt = ReadDeletionTimeUtc(meta)
                                ?? File.GetCreationTimeUtc(meta);
                            if (deletedAt > cutoff) continue;

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
                                try { size = new FileInfo(data).Length; } catch { }
                                File.Delete(data);
                            }
                            else if (Directory.Exists(data))
                            {
                                if (NativeSafety.IsReparsePoint(data)) continue;
                                size = DirectorySize(data);
                                Directory.Delete(data, recursive: true);
                            }
                            File.Delete(meta);
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
    /// timestamp in the same relative order, but the timestamp sits 12 bytes in
    /// for version 1 (4-byte version) and 16 in for version 2 (8-byte version), so
    /// the version is what selects the offset.
    /// </para></summary>
    internal static DateTime? ReadDeletionTimeUtc(string metaPath)
    {
        try
        {
            using var stream = File.Open(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[24];
            if (stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24) return null;
            var offset = BitConverter.ToInt32(header, 0) == 1 ? 12 : 16;
            var fileTime = BitConverter.ToInt64(header, offset);
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

    private static long DirectorySize(string path)
    {
        try
        {
            return FileEnumeration.Files(path)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}
