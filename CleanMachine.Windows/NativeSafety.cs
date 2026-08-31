namespace CleanMachine.Windows;

public static class NativeSafety
{
    public static bool IsProtectedPath(string path)
    {
        if (!TryGetFullPath(path, out var full)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return IsWithin(full, windows) || IsWithin(full, system) || (IsWithin(full, user) && !IsWithin(full, Path.GetTempPath()));
    }

    public static bool IsSafeFileCandidate(string path, string? allowedRoot = null)
    {
        if (!TryGetFullPath(path, out var full) || IsProtectedPath(full) || IsReparsePoint(full)) return false;
        return string.IsNullOrWhiteSpace(allowedRoot) || TryGetFullPath(allowedRoot, out var root) && IsWithin(full, root);
    }

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
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
