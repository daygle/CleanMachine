using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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

    /// <summary>A stable suffix identifying the current user, appended to every
    /// kernel object name.
    /// <para>
    /// <c>Local\</c> scopes a name to the logon session, not to the account, so
    /// two people sharing a session (RDP, a shared or family machine) would
    /// otherwise share one mutex and one pair of events: either could suppress
    /// the other's app from launching, and either could signal it to exit. A
    /// per-user suffix removes the collision.
    /// </para>
    /// <para>
    /// The suffix is the naming half of the fix; <see cref="RestrictToCurrentUser"/>
    /// is the half that actually enforces it, because a name alone is not a
    /// boundary - a process as this user can derive the same suffix. Neither half
    /// stops code already running as this user, which is a limit of the process
    /// model rather than of this code.</para></summary>
    internal static string UserScope { get; } = ComputeUserScope();

    /// <summary>Builds a DACL granting full control to this user and to SYSTEM,
    /// and to nobody else.
    /// <para>
    /// A named kernel object with no explicit security descriptor gets one derived
    /// from the creating token, which in practice means another account in the
    /// same session can open and signal it. These objects are a control channel -
    /// setting the shutdown event makes the running app exit - so they are created
    /// with an explicit, minimal ACL instead. A fresh ObjectSecurity starts with no
    /// ACEs, so the two allow rules below are the whole policy: anything not named
    /// is denied.
    /// </para></summary>
    private static TSecurity RestrictToCurrentUser<TSecurity>(TSecurity security) where TSecurity : ObjectSecurity
    {
        // SYSTEM, because a scheduled task or an installer helper may legitimately
        // need to reach these. Administrators are deliberately NOT granted: the app
        // is per-user and runs unelevated, so nothing in it needs that reach.
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new SystemAccessRule(
            system, SystemAccessRights.FullControl, AccessControlType.Allow));

        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
            security.AddAccessRule(new SystemAccessRule(
                user, SystemAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The kernel object name for <paramref name="baseName"/>, scoped to
    /// the current user and optionally to a test scope so unit tests never touch
    /// the live app's objects.
    /// <para>
    /// Every caller must go through here rather than using the bare constants:
    /// the unscoped name is the one the app must not use.</para></summary>
    public static string Name(string baseName, string? scope = null)
    {
        var scoped = $"{baseName}.{UserScope}";
        return scope is null ? scoped : $"{scoped}.{scope}";
    }

    private static string ComputeUserScope()
    {
        // The per-user application-data path is stable for the account, and -
        // unlike a user name - survives the account being renamed. It is hashed
        // only so a filesystem path does not end up spelled out in a globally
        // visible kernel object name.
        var perUser = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(perUser)) return "0";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(perUser)))[..16];
    }

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
            var mutex = new Mutex(
                initiallyOwned: true, Name(MutexName, scope), out var createdNew,
                RestrictToCurrentUser(new MutexSecurity()));
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
            using var handle = EventWaitHandle.OpenExisting(Name(ShutdownEventName));
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
            using var handle = EventWaitHandle.OpenExisting(Name(ActivateEventName));
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
            // EventWaitHandleAcl rather than EventWaitHandle: this is the only way
            // to hand the object an explicit DACL instead of the one inherited from
            // this token.
            var shutdown = new EventWaitHandleAcl(
                initialState: false, mode: EventResetMode.AutoReset, Name(ShutdownEventName, scope),
                RestrictToCurrentUser(new EventWaitHandleSecurity()));
            var activate = new EventWaitHandleAcl(
                initialState: false, mode: EventResetMode.AutoReset, Name(ActivateEventName, scope),
                RestrictToCurrentUser(new EventWaitHandleSecurity()));
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
