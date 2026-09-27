using Microsoft.Win32;

namespace CleanMachine.Windows;

/// <summary>Owns the HKCU Run value that starts CleanMachine at logon.
/// <para>
/// The value is rewritten whenever it goes stale. For an MSIX install the
/// registered path is <c>Environment.ProcessPath</c>, which lives in a
/// version-stamped package folder under WindowsApps that the Store deletes on
/// every update. Without a repair pass the Run value survives pointing at a
/// folder that no longer exists: the app stops auto-starting, and Registry Care
/// flags the app's own entry as a dead startup reference.
/// </para></summary>
public static class StartupRegistration
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "CleanMachine";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Switch = "--background";

    public static void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled) Write(executablePath, RunPath, ValueName);
        else RemoveCore(RunPath, ValueName);
    }

    /// <summary>Brings the Run value in line with the user's choice, repairing a
    /// stale path but never overriding that choice: nothing is written when the
    /// user has startup off, and nothing is removed when they have it on.
    /// Returns true when the registry was actually changed.</summary>
    internal static bool Sync(bool shouldRun, string? executablePath)
        => SyncCore(shouldRun, executablePath, RunPath, ValueName);

    private static bool SyncCore(bool shouldRun, string? executablePath, string runPath, string valueName)
    {
        if (!shouldRun)
        {
            if (ReadCommand(runPath, valueName) is null) return false;
            RemoveCore(runPath, valueName);
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

        var current = ReadCommand(runPath, valueName);
        if (current is not null)
        {
            // Only rewrite our own value when it is unusable. A live path that
            // merely differs (a second install, say) is left alone rather than
            // fought over on every launch.
            var target = ResolveExecutable(current);
            if (target is not null && File.Exists(target)) return false;
        }

        Write(executablePath, runPath, valueName);
        return true;
    }

    public static bool IsEnabled() => ReadCommand(RunPath, ValueName) is not null;

    /// <summary>Test seam over <see cref="Sync"/>: the production path is hardcoded
    /// to the app's own value name, so tests need to redirect it at a scratch key
    /// rather than mutating the live Run entry on the machine running the suite.
    /// The repair decision itself - the part that can silently regress - is the
    /// same code either way.</summary>
    internal static bool SyncForTest(bool shouldRun, string? executablePath, string runPath, string valueName)
        => SyncCore(shouldRun, executablePath, runPath, valueName);

    private static void Write(string executablePath, string runPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(runPath);
        key.SetValue(valueName, $"\"{executablePath}\" {Switch}");
    }

    /// <summary>Deletes the Run value and the matching StartupApproved blob.
    /// Explorer keys its enabled/disabled state by the value name, so leaving
    /// the blob behind leaves stale state in Task Manager for an entry that no
    /// longer exists - and a later re-enable would inherit the old state.</summary>
    private static void RemoveCore(string runPath, string valueName)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(runPath, writable: true))
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true))
            approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static string? ReadCommand(string runPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runPath, writable: false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>The executable a stored Run command points at, or null when it
    /// cannot be resolved to a concrete local path (an env var, a bare name, or
    /// a Store activation). Mirrors the resolver the cleanup scanners use so both
    /// agree on what counts as a real path.</summary>
    private static string? ResolveExecutable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        string? candidate;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            candidate = end > 1 ? trimmed[1..end] : null;
        }
        else
        {
            candidate = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
        if (string.IsNullOrEmpty(candidate) || candidate.Contains('%')) return null;
        return Path.IsPathFullyQualified(candidate) ? candidate : null;
    }
}
