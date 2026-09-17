using System.IO.Enumeration;

namespace CleanMachine.Windows;

/// <summary>Resilient recursive enumeration for cache and temp locations. Unlike
/// Directory.EnumerateFiles(..., SearchOption.AllDirectories), which throws
/// mid-iteration when it meets an access-denied subdirectory (killing the whole
/// scan or clean), these walkers skip what they cannot read: one protected folder
/// (e.g. INetCache\Content.IE5) no longer erases the entire result. Reparse points
/// are never followed, preserving the app's junction protection. Enumeration is
/// lazy so callers that cap their UI drill-downs stop early.</summary>
internal static class FileEnumeration
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>The path itself when it is a file; otherwise every file under it.</summary>
    public static IEnumerable<string> Files(string path)
    {
        if (File.Exists(path)) return [path];
        if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", Options);
        return [];
    }

    /// <summary>Every subdirectory under <paramref name="path"/> (lazy).</summary>
    public static IEnumerable<string> Directories(string path)
        => Directory.Exists(path) ? Directory.EnumerateDirectories(path, "*", Options) : [];
}
