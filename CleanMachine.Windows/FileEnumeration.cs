namespace CleanMachine.Windows;

/// <summary>Lazy, cancellation-aware bounded walkers. No reparse roots or nested
/// links are followed. Inaccessible folders are skipped; excessive breadth,
/// depth or elapsed work is an explicit failure rather than an understated total.</summary>
internal static class FileEnumeration
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static IEnumerable<string> Files(string path, CancellationToken token = default)
        => Walk(path, directories: false, token);
    public static IEnumerable<string> Directories(string path, CancellationToken token = default)
        => Walk(path, directories: true, token);

    private static IEnumerable<string> Walk(string path, bool directories, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (NativeSafety.IsReparsePoint(path)) yield break;
        if (File.Exists(path)) { if (!directories) yield return path; yield break; }
        if (!Directory.Exists(path)) yield break;
        var budget = new WorkBudget(token);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((path, 0));
        while (pending.TryPop(out var folder))
        {
            budget.Check(folder.Depth);
            if (NativeSafety.IsReparsePoint(folder.Path)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder.Path, "*", Options))
            {
                budget.Check(folder.Depth);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push((entry, folder.Depth + 1));
                    if (directories) yield return entry;
                }
                else if (!directories) yield return entry;
            }
        }
    }
}
