using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

/// <summary>Brings CleanMachine back after the Microsoft Store updates it.
/// <para>
/// A Store update closes the running app so the package can be replaced, and
/// nothing starts it again: the background services (browser-exit cleaning,
/// low-disk monitoring, idle and Recycle Bin cleanup) silently stop until the
/// next sign-in. Windows restarts a packaged app after an update only if the app
/// registered for it with <c>RegisterApplicationRestart</c>, which is what this
/// does. Windows only honours the registration for a process that has been
/// running for at least 60 seconds, so an update that lands within a minute of
/// launch still leaves the app closed.
/// </para>
/// <para>
/// The restart is limited to updates: crashes, hangs and reboots are excluded,
/// so a crash loop cannot keep relaunching the app and a reboot is left to the
/// startup task the user controls. The relaunch goes straight to the tray, the
/// same as a logon start, so an update never pops a window up unasked.
/// </para></summary>
internal static class UpdateRestart
{
    internal const string Arguments = "--background";

    // RegisterApplicationRestart flags (winbase.h).
    private const uint RestartNoCrash = 0x1;
    private const uint RestartNoHang = 0x2;
    private const uint RestartNoReboot = 0x8;

    /// <summary>Restart on update only: never after a crash, a hang, or a reboot.
    /// RESTART_NO_PATCH (0x4) is deliberately absent - a package update is the
    /// "patch" case, and excluding it would switch the whole feature off.</summary>
    internal const uint Flags = RestartNoCrash | RestartNoHang | RestartNoReboot;

    /// <summary>Registers this process for restart after an update. Only the GUI
    /// instance calls this; a headless scheduled run or a --shutdown helper
    /// exits on its own and must never be resurrected. Best-effort.</summary>
    public static void Register()
    {
        try { _ = RegisterApplicationRestart(Arguments, Flags); }
        catch { /* restart after update is a convenience, never a failure */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string pwzCommandline, uint dwFlags);
}
