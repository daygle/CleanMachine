using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Native-only tests intentionally run in Windows CI, not the portable
/// harness. No user data is touched. Symlink creation requires Windows privileges.</summary>
[Collection("Cleaning")]
public sealed class NativeDeletionTests
{
    [Fact]
    public void HandleDeleteRemovesFileAndEmptyDirectoryButNeverLinkedAncestor()
    {
        Assert.True(OperatingSystem.IsWindows(), "Run this native test on Windows.");
        using var scratch = new AuditFollowupTests.Scratch();
        var cache = Directory.CreateDirectory(Path.Combine(scratch.Root, "cache")).FullName;
        var file = Path.Combine(cache, "junk");
        File.WriteAllText(file, "1234");
        Assert.Equal(4, SafeDeletion.DeleteFile(file, cache));
        Assert.False(File.Exists(file));
        SafeDeletion.DeleteEmptyDirectory(cache, scratch.Root);
        Assert.False(Directory.Exists(cache));
        var outside = Directory.CreateDirectory(Path.Combine(scratch.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep"), "valuable");
        var link = Path.Combine(scratch.Root, "link");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Throws<IOException>(() => SafeDeletion.DeleteFile(Path.Combine(link, "keep"), link));
            Assert.Equal("valuable", File.ReadAllText(Path.Combine(outside, "keep")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void HeldDirectoryCannotBeRenamedBeforeHandleDelete()
    {
        Assert.True(OperatingSystem.IsWindows(), "Run this native test on Windows.");
        using var scratch = new AuditFollowupTests.Scratch();
        var cache = Directory.CreateDirectory(Path.Combine(scratch.Root, "cache")).FullName;
        using (SafeDeletion.Pin(cache))
            Assert.Throws<IOException>(() => Directory.Move(cache, cache + "-swapped"));
        Directory.Move(cache, cache + "-swapped");
    }

    [Fact]
    public void TemporaryCleanupRootIgnoresEnvironmentOverride()
    {
        Assert.True(OperatingSystem.IsWindows(), "Run this native test on Windows.");
        var original = Environment.GetEnvironmentVariable("TEMP");
        try
        {
            Environment.SetEnvironmentVariable("TEMP", @"C:\Users\arbitrary-target");
            Assert.NotEqual(Environment.GetEnvironmentVariable("TEMP"), TrustedTemp.Root);
            Assert.Equal("Temp", Path.GetFileName(TrustedTemp.Root));
        }
        finally { Environment.SetEnvironmentVariable("TEMP", original); }
    }
}
