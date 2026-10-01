using Microsoft.Win32;

namespace CleanMachine.Windows;

public sealed record CleanupResult(int ItemsRemoved, long BytesRecovered);
public sealed record RegistryFinding(string Hive, string Path, string Reason, bool LowRisk, int Confidence = 50, string Category = "Other", string? ValueName = null);
/// <param name="FilePath">Where the .reg file is.</param>
/// <param name="CreatedAt">When it was written (the export time when provenance
/// confirms it, otherwise the file's last-write time).</param>
/// <param name="KeyRoot">The HKCU-relative registry root the file holds, as
/// recorded at export. Empty when the file is not provenanced.</param>
/// <param name="Verified">True only when <see cref="BackupProvenance"/> has a
/// record of this file whose recorded hash still matches its bytes. A restore
/// point that is not verified can be listed and deleted, never imported.</param>
public sealed record RegistryBackup(
    string FilePath,
    DateTimeOffset CreatedAt,
    string KeyRoot = "",
    bool Verified = false);

public sealed class CleanupService
{
    public Task<IReadOnlyList<RegistryFinding>> ScanRegistrySafelyAsync(
        CancellationToken cancellationToken = default)
        // Walking many registry keys is disk-bound; callers await this on the UI
        // thread, so the scan itself must run on a worker thread.
        => Task.Run<IReadOnlyList<RegistryFinding>>(() =>
        {
        var findings = new List<RegistryFinding>();
        ScanUninstallEntries(RegistryHive.CurrentUser, findings);
        ScanFileAssociations(RegistryHive.CurrentUser, findings);
        ScanMuiCache(findings);
        ScanStartupEntries(findings);
        ScanSoundAppEvents(findings);
        ScanShellMuiCache(findings);
        ScanUserAppPaths(findings);
        ScanOpenWithProgids(findings);
        ScanOpenWithList(findings);
        ScanCompatibilityAssistant(findings);
        ScanUserFonts(findings);
        ScanContextMenuCommands(findings);
        ScanUserComServers(findings);
        ScanUserPath(findings);
        return findings;
        }, cancellationToken);

    private static void ScanUninstallEntries(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return;
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            var displayName = entry?.GetValue("DisplayName") as string;
            var uninstallString = entry?.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(displayName)) continue;
            if (string.IsNullOrWhiteSpace(uninstallString))
            {
                findings.Add(new RegistryFinding("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                    "Uninstall metadata has no removal command", true, 75, "Installer/Uninstaller"));
                continue;
            }
            // The uninstall command names an uninstaller that is gone: the program
            // is no longer installed, so the leftover entry can't uninstall anything.
            // Only act when we can resolve an absolute local exe (MsiExec and env-var
            // commands resolve to null and are left alone).
            var uninstaller = ResolveStartupExecutable(uninstallString);
            if (uninstaller is not null && !File.Exists(uninstaller))
                findings.Add(new RegistryFinding("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                    $"Uninstaller is missing ({Path.GetFileName(uninstaller)})", true, 70, "Installer/Uninstaller"));
        }
    }

    // Per-user file-type associations (HKCU\Software\Classes\.ext) whose default
    // ProgID has no handler class anywhere in the merged HKCR view (HKLM + HKCU) -
    // a dangling association. Removing the per-user key falls back to the system
    // default, so it only ever undoes a broken override.
    private static void ScanFileAssociations(RegistryHive hive, ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var classes = root.OpenSubKey(@"Software\Classes");
        if (classes is null) return;
        foreach (var ext in classes.GetSubKeyNames())
        {
            if (!ext.StartsWith('.')) continue; // only file-extension keys
            using var extKey = classes.OpenSubKey(ext);
            if (extKey?.GetValue(null) is not string progId || string.IsNullOrWhiteSpace(progId)) continue;
            // Resolve the ProgID against the merged HKCR view. Two orphan cases:
            // the class is entirely absent, or it exists but its open command runs
            // an executable that is gone. Either way the per-user override is broken;
            // removing it falls back to the system default.
            using var handler = Registry.ClassesRoot.OpenSubKey(progId);
            if (handler is null)
                findings.Add(new RegistryFinding("HKCU", $@"Software\Classes\{ext}",
                    $"File type {ext} maps to a missing handler '{progId}'", true, 70, "File Extensions"));
            else if (TryGetMissingOpenCommandExe(progId, out var exe))
                findings.Add(new RegistryFinding("HKCU", $@"Software\Classes\{ext}",
                    $"File type {ext} opens with a missing program ({Path.GetFileName(exe)})", true, 70, "File Extensions"));
        }
    }

    /// <summary>True when a ProgID's shell\open\command resolves to an absolute local
    /// executable that no longer exists. Commands we cannot positively resolve to a
    /// fully-qualified path (MsiExec, rundll32, env-var or store activations) return
    /// false so they are never flagged.</summary>
    private static bool TryGetMissingOpenCommandExe(string progId, out string? exe)
    {
        exe = null;
        using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (command?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) return false;
        var candidate = ResolveStartupExecutable(raw);
        if (candidate is null || File.Exists(candidate)) return false;
        exe = candidate;
        return true;
    }

    // Per-extension "Open with" ProgID suggestions
    // (HKCU\...\Explorer\FileExts\.ext\OpenWithProgids) that reference a class no
    // longer registered anywhere. Each is a single value; deleting it only removes
    // a dead entry from the "Open with" list.
    private const string FileExtsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    private static void ScanOpenWithProgids(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var fileExts = root.OpenSubKey(FileExtsKey);
        if (fileExts is null) return;
        foreach (var ext in fileExts.GetSubKeyNames())
        {
            using var progids = fileExts.OpenSubKey($@"{ext}\OpenWithProgids");
            if (progids is null) continue;
            foreach (var progId in progids.GetValueNames())
            {
                if (string.IsNullOrEmpty(progId)) continue;
                using var handler = Registry.ClassesRoot.OpenSubKey(progId);
                if (handler is null)
                    findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithProgids",
                        $"'Open with' entry for {ext} references a missing handler '{progId}'", true, 70, "Open With", progId));
                else if (TryGetMissingOpenCommandExe(progId, out var exe))
                    findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithProgids",
                        $"'Open with' entry for {ext} runs a missing program ({Path.GetFileName(exe)})", true, 70, "Open With", progId));
            }
        }
    }

    // Per-extension "Open with" application list
    // (HKCU\...\Explorer\FileExts\.ext\OpenWithList) whose entry is an absolute path
    // to a program that no longer exists. Bare executable names (resolved via App
    // Paths / PATH) can't be positively verified, so only full paths are flagged.
    private static void ScanOpenWithList(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var fileExts = root.OpenSubKey(FileExtsKey);
        if (fileExts is null) return;
        foreach (var ext in fileExts.GetSubKeyNames())
        {
            using var list = fileExts.OpenSubKey($@"{ext}\OpenWithList");
            if (list is null) continue;
            foreach (var valueName in list.GetValueNames())
            {
                // MRUList just orders the letters; the a/b/c values hold the programs.
                if (string.IsNullOrEmpty(valueName) || valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                if (list.GetValue(valueName) is not string target || string.IsNullOrWhiteSpace(target)) continue;
                var exe = target.Trim('"');
                if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
                if (File.Exists(exe)) continue;
                findings.Add(new RegistryFinding("HKCU", $@"{FileExtsKey}\{ext}\OpenWithList",
                    $"'Open with' entry for {ext} points to a missing program ({Path.GetFileName(exe)})", true, 70, "Open With", valueName));
            }
        }
    }

    // Program Compatibility Assistant remembers executables it has prompted about,
    // keyed by full exe path. Entries for executables that no longer exist are dead.
    private const string CompatAssistantStoreKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";
    private const string CompatAssistantPersistedKey =
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted";

    private static void ScanCompatibilityAssistant(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        foreach (var keyPath in new[] { CompatAssistantStoreKey, CompatAssistantPersistedKey })
        {
            using var key = root.OpenSubKey(keyPath);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                // Value names are full executable paths; only act on absolute local
                // paths we can positively verify are gone.
                if (string.IsNullOrEmpty(valueName) || valueName.Contains('%') || !Path.IsPathFullyQualified(valueName)) continue;
                if (File.Exists(valueName)) continue;
                findings.Add(new RegistryFinding("HKCU", keyPath,
                    $"Compatibility record for a missing program ({Path.GetFileName(valueName)})", true, 75, "Compatibility Assistant", valueName));
            }
        }
    }

    // Localized UI resource cache (MUI). Windows regenerates it on demand, so the
    // whole key is a safe, self-healing cache to clear.
    private static void ScanMuiCache(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var mui = root.OpenSubKey(@"Control Panel\Desktop\MuiCached");
        if (mui is null || mui.ValueCount == 0) return;
        findings.Add(new RegistryFinding("HKCU", @"Control Panel\Desktop\MuiCached",
            "Localized UI cache that Windows regenerates automatically", true, 80, "MUI Cache"));
    }

    // Current-user auto-start entries (Run / RunOnce) whose executable no longer
    // exists on disk. Deleting a startup entry whose program is gone cannot break
    // anything - the program is simply not there to run.
    private static void ScanStartupEntries(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        foreach (var path in new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
        })
        {
            using var key = root.OpenSubKey(path);
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                // Never offer to delete CleanMachine's own autostart entry. The app
                // re-registers it (StartupRegistration.Sync), so removing it here
                // just made it reappear and read as a failed cleanup. It is also
                // the wrong tool for the job: Settings owns that choice.
                if (path == StartupRegistration.RunPath && valueName == StartupRegistration.ValueName) continue;

                var command = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(command)) continue;
                var exe = ResolveStartupExecutable(command);
                if (exe is null || File.Exists(exe)) continue;
                findings.Add(new RegistryFinding("HKCU", path,
                    $"Startup entry '{valueName}' points to a missing executable", true, 70, "Windows Startup", valueName));
            }
        }
    }

    // Current-user sound event entries whose referenced sound file has been removed.
    // A dead reference is harmless to delete and only silences a missing sound.
    private static void ScanSoundAppEvents(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var apps = root.OpenSubKey(@"AppEvents\Schemes\Apps");
        if (apps is null) return;
        foreach (var appName in apps.GetSubKeyNames())
        {
            using var appKey = apps.OpenSubKey(appName);
            if (appKey is null) continue;
            foreach (var eventName in appKey.GetSubKeyNames())
            {
                using var eventKey = appKey.OpenSubKey(eventName);
                var wav = eventKey?.GetValue(".Default") as string;
                if (string.IsNullOrWhiteSpace(wav) || wav.Contains('%') || !Path.IsPathFullyQualified(wav)) continue;
                if (!File.Exists(wav))
                    findings.Add(new RegistryFinding("HKCU", $@"AppEvents\Schemes\Apps\{appName}\{eventName}",
                        "Sound event references a missing file", true, 70, "Sound AppEvents", ".Default"));
            }
        }
    }

    // Shell MuiCache stores friendly display names for executables, keyed by the
    // executable's full path. Entries whose executable no longer exists are dead
    // cache; Windows rebuilds the cache on demand, so removing them is safe.
    private const string ShellMuiCacheKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    private static void ScanShellMuiCache(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = root.OpenSubKey(ShellMuiCacheKey);
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName)) continue;
            // Value names look like "<full exe path>.FriendlyAppName" or
            // ".ApplicationCompany"; strip the known suffix to get the executable.
            var exe = valueName;
            foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
            {
                if (exe.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    exe = exe[..^suffix.Length];
                    break;
                }
            }
            // Only act on absolute paths we can positively verify are gone; env-var
            // and non-qualified entries are left alone.
            if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
            if (File.Exists(exe)) continue;
            findings.Add(new RegistryFinding("HKCU", ShellMuiCacheKey,
                $"Cached app name for a missing program ({Path.GetFileName(exe)})", true, 75, "Shell Cache", valueName));
        }
    }

    // Per-user App Paths entries whose target executable no longer exists on disk.
    // App Paths only resolves a program name to its full path; a dead entry does
    // nothing but point at a program that is gone.
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static void ScanUserAppPaths(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var appPaths = root.OpenSubKey(AppPathsKey);
        if (appPaths is null) return;
        foreach (var name in appPaths.GetSubKeyNames())
        {
            using var entry = appPaths.OpenSubKey(name);
            var target = entry?.GetValue(null) as string; // (Default) = executable path
            if (string.IsNullOrWhiteSpace(target)) continue;
            var exe = target.Trim('"');
            if (exe.Contains('%') || !Path.IsPathFullyQualified(exe)) continue;
            if (File.Exists(exe)) continue;
            findings.Add(new RegistryFinding("HKCU", $@"{AppPathsKey}\{name}",
                $"App Paths entry '{name}' points to a missing program", true, 75, "App Paths"));
        }
    }

    // Per-user fonts (installed "for this user only") are registered here as
    // name -> full path, normally under %LOCALAPPDATA%\Microsoft\Windows\Fonts.
    // An entry whose file is gone is a font Windows can no longer load. System
    // fonts are registered under HKLM with bare file names and are never seen here.
    internal const string UserFontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static void ScanUserFonts(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = root.OpenSubKey(UserFontsKey);
        if (key is null) return;
        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName)) continue;
            if (key.GetValue(valueName) is not string file || string.IsNullOrWhiteSpace(file)) continue;
            file = file.Trim().Trim('"');
            // A bare file name resolves against the system Fonts folder; only full
            // paths can be positively verified as missing.
            if (!Path.IsPathFullyQualified(file) || File.Exists(file)) continue;
            findings.Add(new RegistryFinding("HKCU", UserFontsKey,
                $"Font '{valueName}' points to a missing file ({Path.GetFileName(file)})", true, 75, "Fonts", valueName));
        }
    }

    // Per-user right-click menu commands registered on the shell's catch-all
    // types. A verb whose command runs a program that is gone shows a menu entry
    // that only produces an error.
    internal static readonly string[] ContextMenuTypes =
        ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive"];

    private static void ScanContextMenuCommands(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        foreach (var type in ContextMenuTypes)
        {
            var shellPath = $@"Software\Classes\{type}\shell";
            using var shell = root.OpenSubKey(shellPath);
            if (shell is not null) ScanShellVerbs(shell, shellPath, findings);
        }
    }

    /// <summary>Flags each verb under <paramref name="shell"/> whose command runs a
    /// missing program. A verb handled by a COM object (DelegateExecute on its
    /// command, or ExplorerCommandHandler on the verb) is skipped: Explorer runs
    /// the handler and ignores the command string, so a stale string there does not
    /// make the entry broken. Internal so the rule is testable on a scratch key.</summary>
    internal static void ScanShellVerbs(RegistryKey shell, string shellPath, ICollection<RegistryFinding> findings)
    {
        foreach (var verb in shell.GetSubKeyNames())
        {
            using var verbKey = shell.OpenSubKey(verb);
            if (verbKey is null || HasValue(verbKey, "ExplorerCommandHandler")) continue;
            using var command = verbKey.OpenSubKey("command");
            if (command is null || HasValue(command, "DelegateExecute")) continue;
            if (command.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
            var exe = ResolveStartupExecutable(raw);
            if (exe is null || File.Exists(exe)) continue;
            findings.Add(new RegistryFinding("HKCU", $@"{shellPath}\{verb}",
                $"Right-click entry '{verb}' runs a missing program ({Path.GetFileName(exe)})", true, 70, "Context Menu"));
        }
    }

    private static bool HasValue(RegistryKey key, string name)
        => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);

    // Per-user COM class registrations whose server file is gone. A class is only
    // flagged when every server it registers resolves to a full local path and
    // none of them exists - see MissingComServer.
    private const string UserClsidKey = @"Software\Classes\CLSID";

    private static void ScanUserComServers(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var clsids = root.OpenSubKey(UserClsidKey);
        if (clsids is null) return;
        foreach (var clsid in clsids.GetSubKeyNames())
        {
            if (!IsGuidKeyName(clsid)) continue;
            using var cls = clsids.OpenSubKey(clsid);
            if (cls is null) continue;
            var servers = new List<(bool IsLocal, string? Raw)>();
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                using var serverKey = cls.OpenSubKey(server);
                if (serverKey is null) continue;
                // Expanded on read (the default), so %LOCALAPPDATA%-style paths
                // are checked against the real location.
                servers.Add((server == "LocalServer32", serverKey.GetValue(null) as string));
            }
            var missing = MissingComServer(servers, File.Exists);
            if (missing is null) continue;
            findings.Add(new RegistryFinding("HKCU", $@"{UserClsidKey}\{clsid}",
                $"COM class points to a missing file ({Path.GetFileName(missing)})", true, 70, "COM Registrations"));
        }
    }

    /// <summary>The missing server file that makes a COM class dead, or null when
    /// the class must be left alone. FAILS CLOSED: deleting the class removes every
    /// server it registers, so it is only flagged when all of them are positively
    /// missing. Any registered server that is empty (a deliberate override - the
    /// Windows 11 classic-menu tweak is one), cannot be resolved to a full local
    /// path (a bare "mscoree.dll"), or exists keeps the whole class.</summary>
    internal static string? MissingComServer(
        IReadOnlyList<(bool IsLocal, string? Raw)> servers, Func<string, bool> fileExists)
    {
        string? missing = null;
        foreach (var (isLocal, raw) in servers)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var file = isLocal ? ResolveStartupExecutable(raw) : raw.Trim().Trim('"');
            if (file is null || file.Contains('%') || !Path.IsPathFullyQualified(file)) return null;
            if (fileExists(file)) return null;
            missing ??= file;
        }
        return missing;
    }

    /// <summary>True for a key name in registry GUID form, "{xxxxxxxx-...}".</summary>
    internal static bool IsGuidKeyName(string name)
        => name.Length == 38 && name[0] == '{' && name[^1] == '}' && Guid.TryParseExact(name, "B", out _);

    // Folders on the per-user PATH that no longer exist. Report only: PATH is a
    // single value shared by every entry, and rewriting it automatically is not
    // worth the risk, so these findings are never eligible for removal.
    internal const string UserEnvironmentKey = "Environment";

    private static void ScanUserPath(ICollection<RegistryFinding> findings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var env = root.OpenSubKey(UserEnvironmentKey);
        if (env?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return;
        foreach (var entry in MissingPathFolders(raw, Directory.Exists))
            findings.Add(new RegistryFinding("HKCU", UserEnvironmentKey,
                $"PATH lists a folder that no longer exists ({entry}). Remove it in Settings > System > About > Advanced system settings > Environment Variables.",
                false, 40, "PATH (Report Only)", entry));
    }

    /// <summary>The entries of a PATH string whose folder does not exist, as
    /// written (unexpanded). Entries that do not expand to a full local path are
    /// skipped, since they cannot be positively verified; duplicates are reported
    /// once.</summary>
    internal static IReadOnlyList<string> MissingPathFolders(string path, Func<string, bool> directoryExists)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in path.Split(';'))
        {
            var entry = part.Trim().Trim('"').Trim();
            if (entry.Length == 0 || !seen.Add(entry)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(entry);
            if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded)) continue;
            // UNC folders may simply be offline; never call them missing.
            if (expanded.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            if (!directoryExists(expanded)) missing.Add(entry);
        }
        return missing;
    }

    /// <summary>Extracts the fully-qualified executable a command line runs (a Run
    /// value, an uninstall string, a shell or COM command), or null when it cannot
    /// be resolved: environment variables, relative paths and bare command names are
    /// never resolved, so the scanners only flag entries they can positively verify
    /// as missing.
    /// <para>
    /// Windows accepts an unquoted path with spaces ("C:\Program Files\App\app.exe
    /// -min"). Cutting such a command at its first space read it as "C:\Program",
    /// which does not exist, so a working entry was reported as pointing at a
    /// missing program - and a Quick Clean could delete it. An unquoted command is
    /// therefore read up to the first ".exe" that ends a token. A command with no
    /// spaces at all is taken whole; any other unquoted form is ambiguous and
    /// resolves to null.
    /// </para></summary>
    internal static string? ResolveStartupExecutable(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        string? candidate;
        if (trimmed.StartsWith('"'))
        {
            // Quoted form: the path runs to the NEXT quote. An unterminated quote
            // yields -1, which is not a valid slice bound, so it resolves to null
            // rather than throwing.
            var end = trimmed.IndexOf('"', 1);
            candidate = end > 1 ? trimmed[1..end] : null;
        }
        else if (!trimmed.Contains(' '))
        {
            candidate = trimmed;
        }
        else
        {
            candidate = null;
            for (var at = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                 at >= 0;
                 at = trimmed.IndexOf(".exe", at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var stop = at + 4;
                if (stop == trimmed.Length || trimmed[stop] == ' ')
                {
                    candidate = trimmed[..stop];
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(candidate) || candidate.Contains('%')) return null;
        return Path.IsPathFullyQualified(candidate) ? candidate : null;
    }
}
