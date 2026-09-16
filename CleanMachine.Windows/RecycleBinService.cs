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
                            // The $I file is created when the item is sent to the bin, so its
                            // creation time is the deletion time.
                            if (File.GetCreationTimeUtc(meta) > cutoff) continue;

                            // Matching data entry: "$I<rest>" -> "$R<rest>".
                            var data = Path.Combine(sidDir, "$R" + Path.GetFileName(meta)[2..]);
                            long size = 0;
                            if (File.Exists(data))
                            {
                                try { size = new FileInfo(data).Length; } catch { }
                                File.Delete(data);
                            }
                            else if (Directory.Exists(data))
                            {
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
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}
