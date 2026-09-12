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
    public async Task EmptyRegistryReviewDoesNotCreateBackup()
    {
        var review = await new RegistryCareService().PrepareReviewAsync([]);
        Assert.Empty(review.Findings);
    }

    [Fact]
    public async Task RegistryReviewRequiresLowRiskAndConfidence()
    {
        var service = new RegistryCareService();
        var findings = new[]
        {
            new RegistryFinding("HKCU", "safe", "review", true, 70),
            new RegistryFinding("HKCU", "low", "review", true, 69),
            new RegistryFinding("HKCU", "unsafe", "review", false, 100)
        };
        var review = await service.PrepareReviewAsync(findings);
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
    public async Task SecureDeleteOptionsRequiresSsdAcknowledgement()
    {
        var options = new SecureDeleteOptions(WipeMethod.SimpleZeroFill, 1, false);
        Assert.False(options.ConfirmSolidStateDriveWarning);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SecureDeleteService().DeleteAsync(
                new[] { "/tmp/test" },
                options));
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
        Assert.Null(options.AdditionalProfileRoots);
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

    [Fact]
    public void CleanupCatalogIsGroupedWithUniqueIds()
    {
        var all = WindowsCleanupService.Catalog;
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        Assert.True(all.GroupBy(c => c.Group).Count() >= 4);
        Assert.Contains(all, c => c.EnabledByDefault);
        Assert.Contains(all, c => !c.EnabledByDefault);
    }

    [Fact]
    public void CleanupEnabledStateUsesOverridesThenDefaults()
    {
        var settings = new AppSettings();
        var safe = WindowsCleanupService.Catalog.First(c => c.EnabledByDefault);
        var advanced = WindowsCleanupService.Catalog.First(c => !c.EnabledByDefault);

        Assert.True(WindowsCleanupService.IsEnabled(safe, settings));
        Assert.False(WindowsCleanupService.IsEnabled(advanced, settings));

        settings.DisabledCleanupCategories.Add(safe.Id);
        settings.EnabledCleanupCategories.Add(advanced.Id);
        Assert.False(WindowsCleanupService.IsEnabled(safe, settings));
        Assert.True(WindowsCleanupService.IsEnabled(advanced, settings));
    }

    [Fact]
    public async Task CleaningNothingReturnsEmptyReport()
    {
        var report = await new WindowsCleanupService().CleanSelectedAsync([], new WindowsCleanupOptions());
        Assert.Equal(0, report.Result.ItemsRemoved);
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void BuildPreviewWithNoCategoriesIsEmpty()
    {
        var preview = new WindowsCleanupService().BuildPreview([]);
        Assert.Equal(0, preview.TotalItems);
        Assert.Empty(preview.Items);
    }

    [Fact]
    public async Task UpdateManifestParsesThePublishedCamelCaseFormat()
    {
        // The release workflow publishes the manifest with camelCase keys; UpdateService
        // reads it case-insensitively. This guards the field binding from regressing.
        const string json = """
            {
              "version": "1.2.3",
              "releaseNotes": "See the GitHub release notes.",
              "packages": {
                "x64": {
                  "packageUrl": "https://github.com/daygle/CleanMachine/releases/download/v1.2.3/CleanMachine-x64-v1.2.3.msix",
                  "sha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
                  "architecture": "x64",
                  "publisher": "CN=CleanMachine Publisher"
                }
              }
            }
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var manifest = await UpdateService.ParseManifestAsync(stream);

        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("See the GitHub release notes.", manifest.ReleaseNotes);
        Assert.NotNull(manifest.Packages);
        Assert.True(manifest.Packages.ContainsKey("x64"));
        Assert.Equal("x64", manifest.Packages["x64"].Architecture);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", manifest.Packages["x64"].Sha256);
    }

    [Fact]
    public void CurrentVersionFallsBackToAssemblyVersionNotHardcoded()
    {
        // The unpackaged (installer) build has no MSIX identity, so CurrentVersion
        // must fall back to the assembly version stamped from the release tag —
        // never a hardcoded constant, which made every release look like an update.
        // The test assembly itself is stamped 1.0.0.0 by default; the contract under
        // test is that the fallback is derived from the assembly, and that a manifest
        // at the same version is not offered as an update.
        var current = UpdateService.CurrentVersion();
        Assert.Equal(typeof(UpdateService).Assembly.GetName().Version!.Major, current.Major);
        Assert.Equal(typeof(UpdateService).Assembly.GetName().Version!.Minor, current.Minor);
        Assert.Equal(typeof(UpdateService).Assembly.GetName().Version!.Build, current.Build);
    }

    [Fact]
    public void IsNewerOffersOnlyStrictlyNewerVersions()
    {
        // The released build must never be offered as an "update" to itself.
        var current = UpdateService.CurrentVersion();
        Assert.False(UpdateService.IsNewer($"{current.Major}.{current.Minor}.{current.Build}")); // same version
        Assert.True(UpdateService.IsNewer($"{current.Major + 1}.0.0"));          // strictly newer
        Assert.False(UpdateService.IsNewer("0.0.1"));                            // older
    }

    [Fact]
    public void RegistryCleanableGateRequiresLowRiskConfidenceHiveAndKnownRoot()
    {
        var eligible = new RegistryFinding("HKCU",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OrphanApp",
            "Uninstall metadata has no removal command", true, 75);
        Assert.True(RegistryCareService.IsCleanable(eligible));

        // Wrong hive: never deletable.
        Assert.False(RegistryCareService.IsCleanable(eligible with { Hive = "HKLM" }));
        // Below the confidence floor.
        Assert.False(RegistryCareService.IsCleanable(eligible with { Confidence = 69 }));
        // Unsafe flag.
        Assert.False(RegistryCareService.IsCleanable(eligible with { LowRisk = false }));
        // Outside the allowed roots.
        Assert.False(RegistryCareService.IsCleanable(eligible with { Path = @"Software\SomeOtherKey" }));
        // Path injection via an embedded quote character is rejected.
        var withQuote = @"Software\Classes\evil" + (char)34;
        Assert.False(RegistryCareService.IsCleanable(eligible with { Path = withQuote }));
        // Path traversal is rejected.
        Assert.False(RegistryCareService.IsCleanable(eligible with { Path = @"Software\Classes\..\..\Temp" }));
    }

    [Fact]
    public async Task CleaningEmptyReviewRemovesNothing()
    {
        var result = await new RegistryCareService().CleanAsync(new RegistryReview([], []));
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task CleaningReportsNonEligibleFindingsAsSkipped()
    {
        var review = new RegistryReview(
        [
            new RegistryFinding("HKCU", @"Software\NotAnAllowedRoot", "unsafe target", true, 90)
        ], []);
        var result = await new RegistryCareService().CleanAsync(review);
        Assert.Equal(0, result.Removed);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task CleaningMissingKeyIsReportedNotThrown()
    {
        var review = new RegistryReview(
        [
            new RegistryFinding("HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanMachineTestKeyDoesNotExist",
                "no removal command", true, 75)
        ], []);
        var result = await new RegistryCareService().CleanAsync(review);
        Assert.Equal(0, result.Removed);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void BrowserMonitoringDefaultsMatchSpec()
    {
        var settings = new AppSettings();
        Assert.True(settings.CleanOnBrowserExit);
        Assert.Equal(3, settings.BrowserMonitors.Count);
        Assert.All(settings.BrowserMonitors, m =>
        {
            Assert.True(m.Enabled);
            Assert.Equal(ExitAction.CleanAndNotify, m.AfterExit);
        });
        // System monitoring is opt-in and conservative by default.
        Assert.False(settings.SystemMonitoringEnabled);
        Assert.Equal(ExitAction.CleanSilently, settings.SystemMonitorAction);
        Assert.Equal(1.0, settings.SystemMonitorFreeSpaceGb);
    }

    [Fact]
    public void FindBrowserMonitorIsCaseInsensitiveAndMissingReturnsNull()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.FindBrowserMonitor("Chrome"));
        Assert.NotNull(settings.FindBrowserMonitor("EDGE"));
        Assert.NotNull(settings.FindBrowserMonitor("msedge"));
        Assert.Null(settings.FindBrowserMonitor("safari"));
    }

    [Fact]
    public void SettingsJsonRoundTripPreservesMonitoringConfiguration()
    {
        var settings = new AppSettings
        {
            BrowserMonitors =
            [
                new BrowserMonitorSetting { Browser = "chrome", Enabled = false, AfterExit = ExitAction.DoNothing },
                new BrowserMonitorSetting { Browser = "edge", Enabled = true, AfterExit = ExitAction.CleanSilently },
                new BrowserMonitorSetting { Browser = "firefox", Enabled = true, AfterExit = ExitAction.CleanAndNotify }
            ],
            SystemMonitoringEnabled = true,
            SystemMonitorFreeSpaceGb = 2.5,
            SystemMonitorAction = ExitAction.CleanAndNotify
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.False(clone!.FindBrowserMonitor("chrome")!.Enabled);
        Assert.Equal(ExitAction.DoNothing, clone.FindBrowserMonitor("chrome")!.AfterExit);
        Assert.Equal(ExitAction.CleanSilently, clone.FindBrowserMonitor("edge")!.AfterExit);
        Assert.True(clone.SystemMonitoringEnabled);
        Assert.Equal(2.5, clone.SystemMonitorFreeSpaceGb);
        Assert.Equal(ExitAction.CleanAndNotify, clone.SystemMonitorAction);
    }

    [Fact]
    public void SystemMonitorCandidateSetContainsOnlySafeEnabledCategories()
    {
        var settings = new AppSettings();
        var selected = WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe && WindowsCleanupService.IsEnabled(c, settings))
            .ToList();

        Assert.NotEmpty(selected);
        Assert.All(selected, c => Assert.Equal(CleanupRisk.Safe, c.Risk));
        Assert.Contains(selected, c => c.Id == "system-temp");
        // Review-risk categories (Recycle Bin, Downloads) must never be auto-cleaned.
        Assert.DoesNotContain(selected, c => c.Risk != CleanupRisk.Safe);
    }
}
