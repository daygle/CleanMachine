using System.Runtime.InteropServices;
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
            link = new CShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0 /* STGM_READ */);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var raw = buffer.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : Environment.ExpandEnvironmentVariables(raw);
        }
        catch { return null; }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }
}
