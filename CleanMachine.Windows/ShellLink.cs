using System.Runtime.InteropServices;
using System.Text;

namespace CleanMachine.Windows;

/// <summary>The shell's <c>.lnk</c> COM object, declared once for every caller:
/// <see cref="BrokenShortcuts"/> reads link targets and MainWindow writes the
/// desktop shortcut.
/// <para>
/// A ComImport interface is bound to the native vtable purely by declaration
/// order, so a method out of place silently calls a different native method.
/// MainWindow used to carry its own copy of <see cref="IPersistFile"/> with
/// GetCurFile where IPersist's GetClassID belongs; Save only worked because it
/// happened to land in the right slot regardless. Keeping a single, correctly
/// ordered declaration removes that trap.
/// </para></summary>
internal static class ShellLink
{
    /// <summary>STGM_READ, for <see cref="IPersistFile.Load"/>.</summary>
    internal const int StgmRead = 0;

    /// <summary>SLGP_RAWPATH: the stored path, environment variables unexpanded.</summary>
    internal const int SlgpRawPath = 0x4;

    /// <summary>Creates a new, empty shell link object. Release it with
    /// <see cref="Release"/> when done.</summary>
    internal static object Create() => new CShellLink();

    /// <summary>Releases the COM object behind a link created by <see cref="Create"/>.</summary>
    internal static void Release(object? link)
    {
        if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
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

    /// <summary>IPersistFile, including the inherited IPersist::GetClassID slot.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }
}
