using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the data-root helpers behind MSIX uninstall keep/remove:
/// stripping the package redirection from %LOCALAPPDATA%, the copy of package-local
/// data into the durable user-profile folder (including the properties that keep it
/// safe to run on every launch), and the deferred package-removal command, whose
/// quoting must be airtight because it embeds the package identity.</summary>
public sealed class AppDataPathsTests
{
    [Fact]
    public void RealLocalAppDataStripsPackageRedirection()
    {
        Assert.Equal(@"C:\Users\glen\AppData\Local",
            AppDataPaths.RealLocalAppData(
                @"C:\Users\glen\AppData\Local\Packages\CleanMachine_1.0.39.0_x64__abc123\LocalCache\Local"));

        // The segment match must be case-insensitive.
        Assert.Equal(@"C:\Users\glen\AppData\Local",
            AppDataPaths.RealLocalAppData(
                @"C:\Users\glen\AppData\Local\packages\SomeFamily_abc\LocalState"));

        // Unpackaged paths contain no \Packages\ segment and pass through.
        Assert.Equal(@"C:\Users\glen\AppData\Local",
            AppDataPaths.RealLocalAppData(@"C:\Users\glen\AppData\Local"));
    }

    /// <summary>The whole point of the durable folder: it must not sit under
    /// %LOCALAPPDATA%, because that is the tree a packaged install has redirected
    /// into the package (and therefore deletes on uninstall).</summary>
    [Fact]
    public void ProfileRootIsOutsideTheRedirectedLocalAppDataTree()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(profile, AppDataPaths.ProfileRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppDataPaths.FolderName, Path.GetFileName(AppDataPaths.ProfileRoot));
        Assert.DoesNotContain(localAppData, AppDataPaths.ProfileRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationCopiesPackageDataToTheDurableFolder()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "Packages", "Family", "LocalCache", "Local", "CleanMachine");
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{\"seed\":true}");

            var root = AppDataPaths.MigrateToDurableRoot(durable, packageLocal);

            Assert.Equal(durable, root);
            Assert.True(File.Exists(Path.Combine(durable, "settings.json")));
            Assert.Equal("{\"seed\":true}", File.ReadAllText(Path.Combine(durable, "settings.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    /// <summary>Windows destroys the package folder at uninstall, so the copy must
    /// never be a move. Losing the only copy of a user's settings is the one
    /// unrecoverable outcome of this whole change.</summary>
    [Fact]
    public void MigrationNeverDeletesTheSource()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(packageLocal, "activity.json"), "[]");

            AppDataPaths.MigrateToDurableRoot(durable, packageLocal);

            Assert.True(Directory.Exists(packageLocal), "package folder must be left intact");
            Assert.True(File.Exists(Path.Combine(packageLocal, "settings.json")));
            Assert.True(File.Exists(Path.Combine(packageLocal, "activity.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    /// <summary>Newer data wins: re-running the migration on every launch must
    /// never resurrect an old settings file the user has since changed.</summary>
    [Fact]
    public void MigrationNeverOverwritesWhatIsAlreadyInTheDurableFolder()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "stale");
            Directory.CreateDirectory(durable);
            File.WriteAllText(Path.Combine(durable, "settings.json"), "current");

            AppDataPaths.MigrateToDurableRoot(durable, packageLocal);

            Assert.Equal("current", File.ReadAllText(Path.Combine(durable, "settings.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    /// <summary>Registry restore points live in a Backups subfolder. If the copy
    /// only walked top-level files the user's restore points would be the first
    /// thing lost.</summary>
    [Fact]
    public void MigrationCarriesSubfoldersIncludingBackups()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        try
        {
            Directory.CreateDirectory(Path.Combine(packageLocal, "Backups"));
            File.WriteAllText(Path.Combine(packageLocal, "Backups", "registry-test.reg"), "REGEDIT4");

            AppDataPaths.MigrateToDurableRoot(durable, packageLocal);

            Assert.True(File.Exists(Path.Combine(durable, "Backups", "registry-test.reg")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void MigrationReadsFromEveryLegacyRootAndCreatesTheDurableFolder()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var real = Path.Combine(scope, "AppData", "Local", "CleanMachine");
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(packageLocal, "activity.json"), "[]");
            File.WriteAllText(Path.Combine(real, "settings.json"), "{}");

            var root = AppDataPaths.MigrateToDurableRoot(durable, packageLocal, real);

            Assert.Equal(durable, root);
            Assert.True(File.Exists(Path.Combine(durable, "activity.json")));
            Assert.True(File.Exists(Path.Combine(durable, "settings.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void MigrationIgnoresAMissingOrUnreadableSource()
    {
        var scope = TestScope();
        var durable = Path.Combine(scope, "Profile", "CleanMachine");
        var missing = Path.Combine(scope, "does-not-exist");
        try
        {
            var root = AppDataPaths.MigrateToDurableRoot(durable, missing, string.Empty);

            Assert.Equal(durable, root);
            Assert.True(Directory.Exists(durable));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    /// <summary>If the durable folder cannot be used the app must fall back to a
    /// location that is actually reachable, rather than to an empty folder it
    /// cannot write - the user would silently lose every setting.</summary>
    [Fact]
    public void MigrationFallsBackToAReachableLegacyFolderWhenTheDurableOneIsUnusable()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var blocked = Path.Combine(scope, "blocked", "CleanMachine"); // parent below is a FILE
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{}");
            Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
            File.WriteAllText(blocked, "not a directory");

            var root = AppDataPaths.MigrateToDurableRoot(blocked, packageLocal);

            Assert.Equal(packageLocal, root);
            Assert.True(File.Exists(Path.Combine(packageLocal, "settings.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void RemovalCommandEscapesThePackageIdentity()
    {
        var command = MsixUninstallService.BuildRemovalCommand("Evil'name'; Stop-Process *; '");

        // The raw identity must never appear unescaped - it sits inside a
        // single-quoted PowerShell literal, where ' is the escape char.
        Assert.DoesNotContain("Evil'name'", command);
        Assert.Contains("Evil''name''", command);
        Assert.Contains("Remove-AppxPackage", command);
        Assert.Contains("Start-Sleep", command);
        Assert.Contains("PackageFullName", command);
    }

    private static string TestScope()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"cm-apptest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scope);
        return scope;
    }
}
