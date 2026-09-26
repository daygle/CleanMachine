namespace CleanMachine.Windows;

/// <summary>Single source of truth for CleanMachine's per-user data folder
/// (settings, statistics, activity history, registry backups).
///
/// The folder is %LOCALAPPDATA%\CleanMachine rather than the package-local one
/// so a reinstall picks up where the user left off, and so uninstalling can
/// honor the promise to keep the data unless the user says otherwise. Under
/// MSIX, Environment.GetFolderPath(LocalApplicationData)
/// resolves INTO the package (...\Packages\&lt;family&gt;\LocalCache\...), which
/// Windows deletes whenever the package is removed - silently and without
/// asking. <see cref="Root"/> therefore strips the redirection and one-time
/// migrates package-local data out to the real folder on first use.</summary>
internal static class AppDataPaths
{
    internal const string FolderName = "CleanMachine";

    /// <summary>The canonical data root, resolved once per process.</summary>
    internal static string Root { get; } = SafeResolve();

    /// <summary>The real (non-package) %LOCALAPPDATA%\CleanMachine folder.</summary>
    internal static string RealRoot => Path.Combine(
        RealLocalAppData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        FolderName);

    /// <summary>The folder GetFolderPath reports under MSIX (inside the package);
    /// identical to <see cref="RealRoot"/> in unpackaged builds.</summary>
    internal static string PackageLocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>Strips the MSIX known-folder redirection: under a packaged app
    /// %LOCALAPPDATA% is ...\Packages\&lt;family&gt;\..., so everything before
    /// that segment is the real %LOCALAPPDATA%. Unpackaged paths pass through
    /// unchanged (they contain no \Packages\ segment).</summary>
    internal static string RealLocalAppData(string localAppData)
    {
        var index = localAppData.IndexOf("\\Packages\\", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? localAppData[..index] : localAppData;
    }

    /// <summary>One-time move of package-local data to the real folder. Returns
    /// the folder the app should use: the real one after a successful migration
    /// (or when there is nothing to move), and the package-local one when the
    /// move fails - keeping the data reachable beats an empty root.</summary>
    internal static string MigrateMsixData(string packageLocal, string real)
    {
        try
        {
            if (!Directory.Exists(packageLocal)) return real;
            if (Directory.Exists(real))
            {
                // Real data wins (e.g. from a previous .exe install); only an
                // empty placeholder folder blocks the move.
                if (Directory.EnumerateFileSystemEntries(real).Any()) return real;
                Directory.Delete(real, recursive: true);
            }
            var parent = Path.GetDirectoryName(real);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            Directory.Move(packageLocal, real);
            return real;
        }
        catch
        {
            return Directory.Exists(packageLocal) ? packageLocal : real;
        }
    }

    private static string SafeResolve()
    {
        try
        {
            var packageLocal = PackageLocalRoot;
            var real = RealRoot;
            if (packageLocal == real || !ScheduleService.IsMsix) return real;
            return MigrateMsixData(packageLocal, real);
        }
        catch
        {
            // Never let a migration hiccup surface as a TypeInitializationException
            // that would take every store down with it.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);
        }
    }
}
