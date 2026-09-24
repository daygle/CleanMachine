using System.Diagnostics;
using System.Threading;

namespace CleanMachine.Windows;

/// <summary>
/// Single-instance guard built on a named mutex. When a second copy of the app
/// starts, it signals the first copy to show its window and exits - so "open the
/// app twice" never forks a second process. The same signalling channel is how
/// the installer/uninstaller closes a running copy: a process started with
/// --shutdown asks the running instance to exit (tray icon and background agent
/// included) and only falls back to killing it when the graceful path fails.
/// </summary>
public static class SingleInstance
{
    /// <summary>Mutex name; per-user because the app installs per-user (PrivilegesRequired=lowest).
    /// The "Local\" prefix scopes it to the current logon session.</summary>
    public const string MutexName = @"Local\CleanMachine.SingleInstance";

    /// <summary>Auto-reset event: the running instance waits on it to exit.</summary>
    public const string ShutdownEventName = @"Local\CleanMachine.Shutdown";

    /// <summary>Auto-reset event: the running instance waits on it to show (and
    /// foreground) its main window.</summary>
    public const string ActivateEventName = @"Local\CleanMachine.Activate";

    /// <summary>How long the installer helper waits for the graceful exit before
    /// falling back to a force kill.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The kernel object name for <paramref name="baseName"/>, optionally
    /// suffixed with a test scope so unit tests never touch the live app's objects.</summary>
    public static string Name(string baseName, string? scope = null) =>
        scope is null ? baseName : $"{baseName}.{scope}";

    /// <summary>Acquires the single-instance mutex. Returns null when another GUI
    /// instance is already running (the caller should activate that instance and
    /// exit). Never locks the user out: on an unexpected failure the app is still
    /// allowed to run.</summary>
    public static Mutex? TryAcquire(string? scope = null)
    {
        try
        {
            // For an existing mutex the initiallyOwned flag is ignored, so this
            // never blocks; createdNew tells us whether we are the first instance.
            var mutex = new Mutex(initiallyOwned: true, Name(MutexName, scope), out var createdNew);
            if (createdNew) return mutex;
            mutex.Dispose(); // another instance owns it
            return null;
        }
        catch
        {
            // Cannot tell whether another instance is running (e.g. the name is
            // taken by other software): run anyway rather than block the user.
            return new Mutex(initiallyOwned: false);
        }
    }

    /// <summary>Signals the running instance to exit cleanly (tray icon, agent, and
    /// window all shut down the same way as the tray menu's Exit). Returns true when
    /// nothing is running anymore - the signal was delivered and honored in time,
    /// or there was no instance to close in the first place.</summary>
    public static bool RequestOtherInstanceExit(string processName = "CleanMachine", TimeSpan? timeout = null)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShutdownEventName);
            handle.Set();
        }
        catch
        {
            // No running instance (or no permission to signal it): report whether
            // a process is still around so the caller can force-kill if needed.
            return !IsProcessRunning(processName);
        }
        // The event was signalled; wait until the instance is actually gone so the
        // caller (installer) can immediately delete or replace its files.
        return WaitForProcessExit(processName, timeout ?? ShutdownTimeout);
    }

    /// <summary>Signals the running instance to show and foreground its window.</summary>
    public static void RequestOtherInstanceActivate()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
            handle.Set();
        }
        catch { /* no instance to activate */ }
    }

    /// <summary>Handles the --shutdown command line: asks a running instance to exit
    /// and force-kills it as a last resort. Returns true when no CleanMachine
    /// process remains. Used by the installer's uninstall step; safe to call
    /// headlessly because this helper process never holds the instance mutex.</summary>
    public static bool HandleShutdownArgument(string processName = "CleanMachine") =>
        RequestOtherInstanceExit(processName) || ForceKill(processName);

    /// <summary>Last-resort termination used only when the graceful signal did not
    /// make the process exit in time (e.g. a hung window). The calling process is
    /// always excluded, since the --shutdown helper has the same executable name.</summary>
    public static bool ForceKill(string processName = "CleanMachine")
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try { if (process.Id != Environment.ProcessId) process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                process.Dispose();
            }
            return WaitForProcessExit(processName, TimeSpan.FromSeconds(5));
        }
        catch { return false; }
    }

    /// <summary>Creates the named events a first instance listens on. Pass a scope
    /// in tests to avoid touching a live instance's events. Returns null when the
    /// events could not be created; the app then runs without IPC signalling.</summary>
    public static InstanceEvents? TryCreateEvents(string? scope = null)
    {
        try
        {
            var shutdown = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, Name(ShutdownEventName, scope));
            var activate = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, Name(ActivateEventName, scope));
            return new InstanceEvents(shutdown, activate);
        }
        catch { return null; }
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            // Exclude this process: the --shutdown helper runs from the very same
            // CleanMachine.exe and must never wait for (or kill) itself. Every
            // Process object is disposed even on an early exit: this helper is
            // polled every 200 ms during a shutdown wait, so leaking handles here
            // would accumulate for seconds at a time.
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId) return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static bool WaitForProcessExit(string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessRunning(processName)) return true;
            Thread.Sleep(200);
        }
        return !IsProcessRunning(processName);
    }
}

/// <summary>The named events a first instance owns and listens on; disposing stops listening.</summary>
public sealed class InstanceEvents : IDisposable
{
    public EventWaitHandle Shutdown { get; }
    public EventWaitHandle Activate { get; }

    public InstanceEvents(EventWaitHandle shutdown, EventWaitHandle activate)
    {
        Shutdown = shutdown;
        Activate = activate;
    }

    public void Dispose()
    {
        Shutdown.Dispose();
        Activate.Dispose();
    }
}
