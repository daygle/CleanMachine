using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

public sealed class ManifestAndSafetyTests
{
    [Theory]
    [InlineData(WipeMethod.SimpleZeroFill, 1)]
    [InlineData(WipeMethod.Dod522022M, 3)]
    [InlineData(WipeMethod.Dod522022MEce, 7)]
    [InlineData(WipeMethod.PeterGutmann, 35)]
    [InlineData(WipeMethod.Custom, 35)]
    public void WipeMethodsHaveExpectedPassBounds(WipeMethod method, int expected)
        => Assert.Equal(expected, new SecureDeleteOptions(method, 99, true).Passes);

    [Fact]
    public void CustomPassesAreClampedToSafeRange()
    {
        Assert.Equal(1, new SecureDeleteOptions(WipeMethod.Custom, 0, true).Passes);
        Assert.Equal(35, new SecureDeleteOptions(WipeMethod.Custom, 100, true).Passes);
    }

    [Fact]
    public void InvalidAndProtectedPathsAreRejected()
    {
        Assert.True(NativeSafety.IsProtectedPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(NativeSafety.IsSafeFileCandidate(string.Empty));
    }

    [Fact]
    public void EmptyRegistryReviewDoesNotCreateBackup()
        => Assert.Empty(new RegistryCareService().PrepareReviewAsync([]).GetAwaiter().GetResult().Findings);

    [Fact]
    public void RegistryReviewRequiresLowRiskAndConfidence()
    {
        var service = new RegistryCareService();
        var findings = new[]
        {
            new RegistryFinding("HKCU", "safe", "review", true, 70),
            new RegistryFinding("HKCU", "low", "review", true, 69),
            new RegistryFinding("HKCU", "unsafe", "review", false, 100)
        };
        var review = service.PrepareReviewAsync(findings).GetAwaiter().GetResult();
        Assert.Single(review.Findings);
        Assert.Equal("safe", review.Findings[0].Path);
    }

    [Fact]
    public void NativeSafetyIsWithinHandlesNestedPaths()
    {
        var temp = Path.GetTempPath();
        Assert.True(NativeSafety.IsWithin(Path.Combine(temp, "sub", "file.txt"), temp));
        Assert.False(NativeSafety.IsWithin("/etc/passwd", temp));
    }

    [Fact]
    public void NativeSafetyIsWithinHandlesRootPath()
    {
        var temp = Path.GetTempPath();
        var trimmed = temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.True(NativeSafety.IsWithin(trimmed, temp));
    }

    [Fact]
    public void NativeSafetyIsReparsePointReturnsTrueOnError()
    {
        // Non-existent path should return true (safe to skip)
        Assert.True(NativeSafety.IsReparsePoint("/nonexistent/path/that/does/not/exist"));
    }

    [Fact]
    public void NativeSafetyTryGetFullPathRejectsInvalid()
    {
        Assert.False(NativeSafety.TryGetFullPath("", out _));
        Assert.False(NativeSafety.TryGetFullPath("\0invalid", out _));
    }

    [Fact]
    public void SecureDeleteOptionsRequiresSsdAcknowledgement()
    {
        var options = new SecureDeleteOptions(WipeMethod.SimpleZeroFill, 1, false);
        Assert.False(options.ConfirmSolidStateDriveWarning);
        Assert.Throws<InvalidOperationException>(() =>
            new SecureDeleteService().DeleteAsync(
                new[] { "/tmp/test" },
                options).GetAwaiter().GetResult());
    }

    [Fact]
    public void UpdateStateTransitionsAreTracked()
    {
        var state = new UpdateState("staged", "/tmp/pkg.msix", null, DateTimeOffset.UtcNow);
        Assert.Equal("staged", state.Status);
        Assert.NotNull(state.PackagePath);
        Assert.Null(state.RollbackPath);
    }

    [Fact]
    public void BrowserCleanupOptionsDefaultsAreReasonable()
    {
        var options = new BrowserCleanupOptions();
        Assert.True(options.RequireBrowsersClosed);
        Assert.Null(options.ExcludedPaths);
        Assert.Null(options.ExitionalProfileRoots);
    }

    [Fact]
    public void BrowserCleanupOptionsSupportsExclusions()
    {
        HashSet<string> excluded = ["/tmp/skipped"];
        var options = new BrowserCleanupOptions(ExcludedPaths: excluded);
        Assert.NotNull(options.ExcludedPaths);
        Assert.Contains("/tmp/skipped", options.ExcludedPaths!);
    }

    [Fact]
    public void WindowsCleanupRiskEnumHasExpectedValues()
    {
        Assert.Equal(0, (int)CleanupRisk.Safe);
        Assert.Equal(1, (int)CleanupRisk.Review);
        Assert.Equal(2, (int)CleanupRisk.Advanced);
    }

    [Fact]
    public void AppSettingsDefaultsAreReasonable()
    {
        var settings = new AppSettings();
        Assert.True(settings.BackgroundAgentEnabled);
        Assert.True(settings.CleanOnBrowserExit);
        Assert.True(settings.CheckForUpdatesAutomatically);
        Assert.Equal(WipeMethod.SimpleZeroFill, settings.SecureDeleteMethod);
        Assert.Contains("chrome", settings.ProtectedBrowsers);
        Assert.Empty(settings.ExcludedPaths);
    }

    [Fact]
    public void CleanupProgressReportsCorrectly()
    {
        var progress = new CleanupProgress("test", 5, 10, 1024);
        Assert.Equal("test", progress.Phase);
        Assert.Equal(5, progress.Completed);
        Assert.Equal(10, progress.Total);
        Assert.Equal(1024, progress.BytesProcessed);
    }

    [Fact]
    public void CleanupIssueContainsPathAndReason()
    {
        var issue = new CleanupIssue("/tmp/file.tmp", "Locked");
        Assert.Equal("/tmp/file.tmp", issue.Path);
        Assert.Equal("Locked", issue.Reason);
    }
}
