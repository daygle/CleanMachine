using System.Text;

namespace CleanMachine.Windows;

/// <summary>Finds the current user's desktop and Start menu shortcuts whose target
/// file or folder no longer exists. Only shortcuts that provably point at a missing
/// path on a local fixed drive count: a shortcut to a network share, a removable
/// drive, a URL or a shell item (Store apps, Control Panel) may simply be
/// unreachable right now, so it is never reported. All-users shortcuts need
/// administrator rights to remove and are left alone.</summary>
internal static class BrokenShortcuts
{
    /// <summary>Where shortcuts are looked for, and whether sub-folders are searched.
    /// The desktop is searched at the top level only: folders on the desktop are the
    /// user's own files, not shortcut collections.</summary>
    internal static IReadOnlyList<(string Root, bool Recurse)> Locations()
    {
        var locations = new List<(string, bool)>();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrEmpty(desktop)) locations.Add((desktop, false));
        if (!string.IsNullOrEmpty(startMenu)) locations.Add((startMenu, true));
        return locations;
    }

    /// <summary>Every broken shortcut in <see cref="Locations"/>, skipping anything
    /// under <paramref name="excludedPaths"/>.</summary>
    public static IReadOnlyList<string> Find(IReadOnlySet<string>? excludedPaths = null)
    {
        var found = new List<string>();
        foreach (var (root, recurse) in Locations())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = recurse,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).ToArray();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var shortcut in shortcuts)
            {
                if (excludedPaths?.Any(excluded => NativeSafety.IsWithin(shortcut, excluded)) == true) continue;
                if (NativeSafety.IsReparsePoint(shortcut)) continue;
                if (IsBrokenTarget(ReadTarget(shortcut), PathExists, IsLocalFixedDrive))
                    found.Add(shortcut);
            }
        }
        return found;
    }

    /// <summary>Whether the path is a .lnk file inside one of <see cref="Locations"/>. Checked again right before a
    /// delete, so nothing outside those folders can ever be removed through this
    /// category.</summary>
    internal static bool IsInLocations(string path)
        => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
           && Locations().Any(location => NativeSafety.IsWithin(path, location.Root));

    /// <summary>The broken-target rule, kept pure so it is unit-testable. A target is
    /// broken only when it is a fully qualified local path on a fixed drive that is
    /// present, and nothing exists at it. Everything uncertain counts as not broken.</summary>
    internal static bool IsBrokenTarget(string? target, Func<string, bool> exists, Func<string, bool> isLocalFixedDrive)
    {
        if (string.IsNullOrWhiteSpace(target)) return false; // URL, Store app or other shell item
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) return false; // network share or device path
        if (!Path.IsPathFullyQualified(target)) return false;
        var root = Path.GetPathRoot(target);
        if (string.IsNullOrEmpty(root) || !isLocalFixedDrive(root)) return false; // removable, network or missing drive
        return !exists(target);
    }

    private static bool PathExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return true; } // fail closed: unknown means "keep the shortcut"
    }

    private static bool IsLocalFixedDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch { return false; }
    }

    /// <summary>The shortcut's target path with environment variables expanded, or
    /// null when it has none (shell items) or cannot be read. The link is only
    /// loaded, never resolved: resolving can search the disk and rewrite the file.</summary>
    private static string? ReadTarget(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        object? link = null;
        try
        {
            link = ShellLink.Create();
            ((ShellLink.IPersistFile)link).Load(shortcutPath, ShellLink.StgmRead);
            var buffer = new StringBuilder(1024);
            ((ShellLink.IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, ShellLink.SlgpRawPath);
            var raw = buffer.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : Environment.ExpandEnvironmentVariables(raw);
        }
        catch { return null; }
        finally { ShellLink.Release(link); }
    }
}
