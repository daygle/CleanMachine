using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the data-root helpers behind MSIX uninstall keep/remove:
/// stripping the package redirection from %LOCALAPPDATA%, the one-time migration
/// of package-local data to the real folder (including its failure fallback),
/// and the deferred package-removal command, whose quoting must be airtight
/// because it embeds the package identity.</summary>
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

    [Fact]
    public void MigrationMovesPackageDataToTheRealFolder()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "Packages", "Family", "LocalCache", "Local", "CleanMachine");
        var real = Path.Combine(scope, "AppData", "Local", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{}");

            var root = AppDataPaths.MigrateMsixData(packageLocal, real);

            Assert.Equal(real, root);
            Assert.True(File.Exists(Path.Combine(real, "settings.json")));
            Assert.False(Directory.Exists(packageLocal));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void MigrationKeepsExistingRealDataAndLeavesTheShadowAlone()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var real = Path.Combine(scope, "real", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "old.json"), "old");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "settings.json"), "{}");

            var root = AppDataPaths.MigrateMsixData(packageLocal, real);

            Assert.Equal(real, root);
            Assert.True(File.Exists(Path.Combine(packageLocal, "old.json")), "shadow copy must not be touched");
            Assert.True(File.Exists(Path.Combine(real, "settings.json")));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void MigrationReplacesAnEmptyRealPlaceholder()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var real = Path.Combine(scope, "real", "CleanMachine");
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{}");
            Directory.CreateDirectory(real); // exists but has no content

            var root = AppDataPaths.MigrateMsixData(packageLocal, real);

            Assert.Equal(real, root);
            Assert.True(File.Exists(Path.Combine(real, "settings.json")));
            Assert.False(Directory.Exists(packageLocal));
        }
        finally { Directory.Delete(scope, recursive: true); }
    }

    [Fact]
    public void MigrationFallsBackToPackageDataWhenTheMoveFails()
    {
        var scope = TestScope();
        var packageLocal = Path.Combine(scope, "package", "CleanMachine");
        var real = Path.Combine(scope, "real", "CleanMachine"); // parent below is a FILE: Move throws
        try
        {
            Directory.CreateDirectory(packageLocal);
            File.WriteAllText(Path.Combine(packageLocal, "settings.json"), "{}");
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            File.WriteAllText(real, "not a directory");

            var root = AppDataPaths.MigrateMsixData(packageLocal, real);

            // The move failed, so the data must stay reachable at the old location.
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
