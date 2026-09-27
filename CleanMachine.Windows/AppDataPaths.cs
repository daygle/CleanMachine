namespace CleanMachine.Windows;

/// <summary>Single source of truth for CleanMachine's per-user data folder
/// (settings, statistics, activity history, registry backups).
///
/// The folder has to survive an uninstall, which rules out %LOCALAPPDATA% on a
/// packaged (MSIX) install for two independent reasons, both verified on a real
/// Store install:
/// <list type="bullet">
/// <item>This process's own writes under %LOCALAPPDATA% are redirected into
/// ...\Packages\&lt;family&gt;\LocalCache\Local\, and Windows deletes that folder
/// when the package is removed - silently, and without asking.</item>
/// <item>Tools this app spawns are NOT redirected, so reg.exe writes to the
/// literal %LOCALAPPDATA% path, which therefore never physically exists and
/// makes every registry backup fail.</item>
/// </list>
/// <para>Stripping the "\Packages\" segment from the known folder (see
/// <see cref="RealLocalAppData"/>) defeats neither of those: the redirector keys
/// off the path, not off the API the path came from, so a hand-built
/// %LOCALAPPDATA% path is still redirected. The durable folder is therefore
/// <see cref="ProfileRoot"/>, under the user profile and outside every known
/// folder that gets redirected, and existing data is copied there on first
/// launch.</para></summary>
internal static class AppDataPaths
{
    internal const string FolderName = "CleanMachine";

    /// <summary>The canonical data root, resolved once per process.</summary>
    internal static string Root { get; } = SafeResolve();

    /// <summary>The durable data folder: the user profile, which is outside every
    /// known folder MSIX redirects, so this process and the tools it spawns both
    /// resolve it to the same real path, and it outlives the package.</summary>
    internal static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderName);

    /// <summary>The non-package %LOCALAPPDATA%\CleanMachine folder: a legacy
    /// location and a migration source. Still the data root for unpackaged builds,
    /// where nothing is redirected.</summary>
    internal static string RealRoot => Path.Combine(
        RealLocalAppData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        FolderName);

    /// <summary>The folder GetFolderPath reports under MSIX (inside the package).
    /// Identical to <see cref="RealRoot"/> in unpackaged builds. A migration source
    /// and the folder Windows deletes at uninstall.</summary>
    internal static string PackageLocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>Strips the MSIX known-folder redirection: under a packaged app
    /// %LOCALAPPDATA% is ...\Packages\&lt;family&gt;\..., so everything before
    /// that segment is the real %LOCALAPPDATA%. Unpackaged paths pass through
    /// unchanged (they contain no \Packages\ segment).
    ///
    /// Kept for the legacy/migration paths and for its own unit test. It is NOT
    /// on its own a way to escape the redirector - see the type summary.</summary>
    internal static string RealLocalAppData(string localAppData)
    {
        var index = localAppData.IndexOf("\\Packages\\", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? localAppData[..index] : localAppData;
    }

    /// <summary>Copies any data from the legacy roots into the durable folder and
    /// returns the folder the app should use.
    ///
    /// Never throws, and never deletes a source. The package copy is the user's
    /// only other copy of their settings and Windows destroys it at uninstall, so
    /// copying is the only safe direction for data that must not be lost. Files
    /// already present in the durable folder are never overwritten, so newer data
    /// always wins and a partially-completed copy is repaired rather than undone.
    /// </summary>
    internal static string MigrateToDurableRoot(string durable, params string[] legacyRoots)
    {
        try
        {
            Directory.CreateDirectory(durable);
            foreach (var legacy in legacyRoots)
            {
                if (string.IsNullOrWhiteSpace(legacy)
                    || string.Equals(legacy, durable, StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyTree(legacy, durable);
            }
            return durable;
        }
        catch
        {
            // The durable folder may be unwritable (locked profile, policy). Fall
            // back to a legacy location that is actually reachable rather than
            // returning a folder the app cannot write.
            return legacyRoots.FirstOrDefault(Directory.Exists) ?? PackageLocalRoot;
        }
    }

    /// <summary>Pushes package-local data out to the durable folder. Called before
    /// the package is removed so that "keep my data" is actually true.</summary>
    internal static string CopyToDurableRoot()
        => MigrateToDurableRoot(ProfileRoot, PackageLocalRoot, RealRoot);

    /// <summary>Copies a legacy tree into the durable folder, file by file, so one
    /// unreadable file never abandons the rest. Subdirectories are carried across
    /// too - the Backups folder holds the user's registry restore points. Depth
    /// is bounded because this runs on every first launch.</summary>
    private static void CopyTree(string source, string target, int depth = 0)
    {
        // File.Copy does not create intermediate directories: without this, every
        // file in a nested folder (the Backups folder of registry restore points)
        // fails with DirectoryNotFoundException - an IOException, so the catch
        // below swallowed it and the restore points were silently dropped.
        Directory.CreateDirectory(target);

        foreach (var file in SafeEnumerate(source, SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination)) continue;
            try { File.Copy(file, destination, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by another process, or vanished between listing and copy.
            }
        }
        if (depth >= 4) return;
        // TopDirectoryOnly, not AllDirectories: recursion below already reaches
        // nested folders, and listing them here too would walk the tree twice.
        foreach (var directory in SafeEnumerate(source, SearchOption.TopDirectoryOnly, directories: true))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)), depth + 1);
        }
    }

    private static IReadOnlyList<string> SafeEnumerate(
        string directory, SearchOption option, bool directories = false)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return directories ? Directory.GetDirectories(directory, "*", option)
                               : Directory.GetFiles(directory, "*", option);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string SafeResolve()
    {
        try
        {
            // Unpackaged: nothing is redirected, so the ordinary per-user
            // application-data location is both correct and durable.
            if (!ScheduleService.IsMsix) return RealRoot;
            // Already migrated: a settings file at the durable location means an
            // earlier launch finished the copy. Re-running it on every launch
            // would resurrect anything the user has since deleted.
            if (File.Exists(Path.Combine(ProfileRoot, "settings.json"))) return ProfileRoot;
            return MigrateToDurableRoot(ProfileRoot, PackageLocalRoot, RealRoot);
        }
        catch
        {
            // Never let a migration hiccup surface as a TypeInitializationException
            // that would take every store down with it.
            return PackageLocalRoot;
        }
    }
}
