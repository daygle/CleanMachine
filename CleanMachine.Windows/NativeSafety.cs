namespace CleanMachine.Windows;

/// <summary>Path guards shared by the cleanup services.
/// <para>
/// These are the building blocks; the services compose them with their own
/// trusted-root allow-lists (see <c>WindowsCleanupService.TrustedCleanupRoots</c>
/// and the browser/app catalogs), which is where "may I delete this" is actually
/// decided.
/// </para></summary>
public static class NativeSafety
{
    /// <summary>Locations that are never a deletion candidate, whatever a caller
    /// asks for. These are system-wide program and OS locations: a cleanup tool
    /// that can write here can break the machine, and no cache any of these hold
    /// is worth that.
    /// <para>
    /// The user's own profile is deliberately NOT on this list. Treating the whole
    /// profile as protected is the obvious-looking rule and it is wrong for this
    /// app: essentially every legitimate target - browser caches, app temp files,
    /// Windows Update downloads - lives under the profile, so such a rule either
    /// refuses all real work or gets quietly special-cased until it protects
    /// nothing. The profile is handled by the caller naming a trusted root
    /// instead, which is a decision about data rather than a guess about folders.
    /// </para></summary>
    private static IEnumerable<string> ProtectedRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    /// <summary>True when the path is inside a protected system location, is a
    /// drive root, or cannot be resolved at all. Fails closed throughout: an
    /// unresolvable path is reported as protected.</summary>
    public static bool IsProtectedPath(string path)
    {
        if (!TryGetFullPath(path, out var full)) return true;
        foreach (var root in ProtectedRoots())
            if (!string.IsNullOrWhiteSpace(root) && IsWithin(full, root)) return true;
        // A drive root ("C:\") is never a file to delete, and treating it as one
        // is how a "clean this folder" call turns into a volume-level operation.
        var volumeRoot = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(volumeRoot)
            && full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>True when a path may be deleted: it resolves, it is not a
    /// protected location, it is not a reparse point, and it is inside
    /// <paramref name="allowedRoot"/>.
    /// <para>
    /// <paramref name="allowedRoot"/> is required, and that is the point. With it
    /// optional, the call degenerates into "delete anything that is not in a
    /// system folder", which is not a safety rule any caller should be able to
    /// reach by omitting an argument. Naming the trusted root is the decision
    /// that makes the check mean something.
    /// </para></summary>
    public static bool IsSafeFileCandidate(string path, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot)) return false;
        if (!TryGetFullPath(path, out var full) || !TryGetFullPath(allowedRoot, out var root)) return false;
        if (IsProtectedPath(full) || !IsWithin(full, root) || IsReparsePoint(full)) return false;
        return true;
    }

    /// <summary>Whether a path is a reparse point (junction, symlink, mount point).
    /// Fails closed: anything that cannot be inspected counts as one, because the
    /// only safe reading of "I could not check" is "skip it".</summary>
    public static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static bool TryGetFullPath(string path, out string full)
    {
        try { full = Path.GetFullPath(path); return !string.IsNullOrWhiteSpace(full); }
        catch (ArgumentException) { full = string.Empty; return false; }
        catch (NotSupportedException) { full = string.Empty; return false; }
    }

    public static bool IsWithin(string path, string parent)
    {
        if (!TryGetFullPath(path, out var full) || !TryGetFullPath(parent, out var root)) return false;
        // GetFullPath preserves a trailing separator, so normalize the parent before comparing;
        // a path equal to the parent (e.g. the parent directory itself) is within it.
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rootTrimmed.Length == 0) return false;
        return full.Equals(rootTrimmed, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
