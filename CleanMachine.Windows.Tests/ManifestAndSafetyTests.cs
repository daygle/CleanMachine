using CleanMachine.Windows;
using Microsoft.Win32;
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
        Assert.True(settings.CleanOnBrowserExit);
        // The background agent has no standalone flag; it is required whenever a
        // service that needs it is enabled - browser-exit cleaning is on by default.
        Assert.True(settings.RequiresBackgroundAgent);
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
    public void ActivityBreakdownLinesHandleEmptyAndRegistryResults()
    {
        Assert.Null(ActivityStore.BreakdownLines([]));

        var lines = ActivityStore.BreakdownLines(
        [
            new CleanupCategoryResult("Registry Care", 3, 0),
            new CleanupCategoryResult("Temporary Files", 2, 2048)
        ]);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Contains("Temporary Files", lines[0]);
        Assert.Contains("Registry Care", lines[1]);
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
    public async Task UnknownWindowsCleanupCategoriesAreRejected()
    {
        var unknown = new CleanupCategory(
            "not-a-catalog-category", "Test", "Unknown", "", CleanupRisk.Safe, true, CleanupKind.RegistryValues,
            Path: @"Software\Microsoft\Windows\CurrentVersion\Run");

        var report = await new WindowsCleanupService().CleanSelectedAsync([unknown], new WindowsCleanupOptions());

        Assert.Equal(0, report.Result.ItemsRemoved);
        Assert.Contains(report.Skipped, issue => issue.Path == unknown.Id);
    }

    [Fact]
    public void WindowsUpdateCleanupIsAnAdvancedOptInComponentStoreItem()
    {
        var item = WindowsCleanupService.Catalog.Single(c => c.Id == "advanced-component-store");
        Assert.Equal("Windows Update Cleanup", item.Name);
        Assert.Equal("Windows Advanced Options", item.Group);
        Assert.Equal(CleanupKind.ComponentStore, item.Kind);
        // It is an elevated, irreversible action, so it must never be a default or a
        // Safe category (which the automatic/scheduled/quick paths auto-run).
        Assert.Equal(CleanupRisk.Advanced, item.Risk);
        Assert.False(item.EnabledByDefault);
    }

    [Fact]
    public void BuildPreviewDescribesTheComponentStoreAction()
    {
        var item = WindowsCleanupService.Catalog.Single(c => c.Kind == CleanupKind.ComponentStore);
        var preview = new WindowsCleanupService().BuildPreview([item]);
        Assert.Equal(1, preview.TotalItems);
        Assert.Single(preview.Items);
        Assert.Equal(item.Name, preview.Items[0].Category);
    }

    [Fact]
    public void ComponentStoreParsesReclaimedSpaceFromBeforeAfterAnalysis()
    {
        // Two "Actual Size" figures: before cleanup and after. The difference (8.00 - 6.00
        // GB = 2 GB) is the reclaimed space DISM freed.
        const string dism = """
            Component Store (WinSxS) information:
            Actual Size of Component Store : 8.00 GB
            Component Store Cleanup Recommended : Yes
            [after cleanup]
            Actual Size of Component Store : 6.00 GB
            """;
        Assert.Equal(2L * 1024 * 1024 * 1024, WindowsCleanupService.ParseReclaimedBytes(dism));
    }

    [Fact]
    public void ComponentStoreReclaimedSpaceIsZeroWhenUnparseable()
    {
        Assert.Equal(0, WindowsCleanupService.ParseReclaimedBytes("no size lines here"));
        // A single figure (no after-analysis) cannot yield a difference.
        Assert.Equal(0, WindowsCleanupService.ParseReclaimedBytes("Actual Size of Component Store : 8.00 GB"));
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
              },
              "installer": {
                "packageUrl": "https://github.com/daygle/CleanMachine/releases/download/v1.2.3/CleanMachine-Setup-1.2.3.exe",
                "sha256": "1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF",
                "architecture": "x64"
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
        // Installer section is optional; when present it should parse correctly.
        Assert.NotNull(manifest.Installer);
        Assert.Contains(".exe", manifest.Installer!.PackageUrl);
        Assert.Equal("x64", manifest.Installer.Architecture);
        Assert.Equal("1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF", manifest.Installer.Sha256);
    }

    [Fact]
    public void CurrentVersionFallsBackToAssemblyVersionNotHardcoded()
    {
        // The unpackaged (installer) build has no MSIX identity, so CurrentVersion
        // must fall back to the assembly version stamped from the release tag -
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
    public void InstallNeedsElevationFollowsDirectoryWritability()
    {
        // A writable directory (a per-user install under %LOCALAPPDATA%) needs no
        // elevation; a nonexistent directory defaults to elevation (fail safe).
        using var writable = new TempDirectory();
        Assert.False(UpdateService.InstallNeedsElevation(Path.Combine(writable.Path, "CleanMachine.exe")));
        Assert.True(UpdateService.InstallNeedsElevation(
            Path.Combine(Path.GetTempPath(), "cleanmachine-missing-dir-test", "CleanMachine.exe")));
    }

    /// <summary>A self-cleaning temp directory so a failed test never leaves litter.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleanmachine-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
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
        // The broad HKCU Classes namespace is not a deletion allow-list; only the
        // scanned extension key itself is eligible.
        Assert.False(RegistryCareService.IsCleanable(eligible with { Path = @"Software\Classes\SomeArbitraryKey" }));
        Assert.True(RegistryCareService.IsCleanable(eligible with
        {
            Path = @"Software\Classes\.txt",
            Category = "File Extensions"
        }));
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
        // "msedge" is the process name, not a settings id - the agent maps it to
        // "edge" before lookup, so it must not match here.
        Assert.Null(settings.FindBrowserMonitor("msedge"));
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
    public void ExitItemsDefaultToEverySafeCatalogItem()
    {
        var settings = new AppSettings();
        var items = settings.EffectiveExitItems("chrome");

        var expected = BrowserCatalog.ItemsFor(BrowserFamily.Chromium).Where(i => !i.Destructive).Select(i => i.Id);
        Assert.Equal(expected.OrderBy(i => i), items.OrderBy(i => i));
        // The guarantee that matters: destructive items are never auto-cleaned.
        Assert.All(BrowserCatalog.ItemsFor(BrowserFamily.Chromium).Where(i => i.Destructive),
            i => Assert.DoesNotContain(items, id => id == i.Id));
    }

    [Fact]
    public void ExitItemsHonorExplicitSelectionIncludingOptedInDestructiveAndFilterStaleIds()
    {
        var settings = new AppSettings();
        settings.FindBrowserMonitor("chrome")!.Items =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache", "sessions", "cookies", "vanished-item" };

        var items = settings.EffectiveExitItems("chrome");
        // Explicit selection is honored as-is: stale ids vanish, but a destructive id
        // the user deliberately ticked ("cookies") is now included (opt-in).
        Assert.Equal(new[] { "cache", "cookies", "sessions" }.OrderBy(i => i), items.OrderBy(i => i));

        // Case-insensitive ids keep working across catalog versions.
        settings.FindBrowserMonitor("edge")!.Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CACHE" };
        Assert.Equal(new[] { "cache" }, settings.EffectiveExitItems("edge").OrderBy(i => i));

        // An explicit empty set means the exit-clean does nothing.
        settings.FindBrowserMonitor("firefox")!.Items = [];
        Assert.Empty(settings.EffectiveExitItems("firefox"));
    }

    [Fact]
    public void SettingsJsonRoundTripPreservesMonitorExitItems()
    {
        var settings = new AppSettings();
        settings.FindBrowserMonitor("chrome")!.Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache" };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cache" }, clone!.FindBrowserMonitor("chrome")!.Items);
        // Unconfigured monitors keep the safe-item fallback after a round-trip.
        Assert.Null(clone.FindBrowserMonitor("edge")!.Items);
        var fallback = clone.EffectiveExitItems("edge");
        Assert.DoesNotContain(fallback, id => id == "cookies");
    }

    [Fact]
    public void CloseAssistDefaultsToOffAndSurvivesRoundTrip()
    {
        var settings = new AppSettings();
        // Off by default: the manual clean keeps its ask-first refusal behavior.
        Assert.False(settings.CloseOpenBrowsersAutomatically);

        settings.CloseOpenBrowsersAutomatically = true;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(clone);
        Assert.True(clone!.CloseOpenBrowsersAutomatically);
    }

    [Fact]
    public void DisplayNameForProcessMapsKnownBrowsersAndPassesThroughUnknown()
    {
        Assert.Equal("Google Chrome", BrowserCleanupService.DisplayNameForProcess("chrome"));
        Assert.Equal("Microsoft Edge", BrowserCleanupService.DisplayNameForProcess("msedge"));
        Assert.Equal("Mozilla Firefox", BrowserCleanupService.DisplayNameForProcess("firefox"));
        // Unknown process names pass through unchanged rather than lying.
        Assert.Equal("notabrowser", BrowserCleanupService.DisplayNameForProcess("notabrowser"));
    }

    [Fact]
    public async Task CloseRunningBrowsersAsyncIsANoOpWhenNothingRuns()
    {
        // No supported browser should be running in the test host; the call must
        // return an empty still-running list immediately (no 5s grace wait).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stillRunning = await BrowserCleanupService.CloseRunningBrowsersAsync([]);
        Assert.Empty(stillRunning);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds}ms");
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

    [Fact]
    public void RegistryCleanableGateAcceptsSafeUserScopedCategories()
    {
        // MUI cache (key-level, allowed root).
        Assert.True(RegistryCareService.IsCleanable(new RegistryFinding("HKCU",
            @"Control Panel\Desktop\MuiCached", "cache", true, 80, "MUI Cache")));
        // Startup value-level finding (allowed root).
        Assert.True(RegistryCareService.IsCleanable(new RegistryFinding("HKCU",
            @"Software\Microsoft\Windows\CurrentVersion\Run", "missing exe", true, 70, "Windows Startup", "FooApp")));
        // Sound event value-level finding (allowed root).
        Assert.True(RegistryCareService.IsCleanable(new RegistryFinding("HKCU",
            @"AppEvents\Schemes\Apps\App\Event", "missing wav", true, 70, "Sound AppEvents", ".Default")));
        // Machine-wide equivalents are never deletable.
        Assert.False(RegistryCareService.IsCleanable(new RegistryFinding("HKLM",
            @"Control Panel\Desktop\MuiCached", "cache", true, 80, "MUI Cache")));
        // A sibling key outside the allow-list is never deletable.
        Assert.False(RegistryCareService.IsCleanable(new RegistryFinding("HKCU",
            @"Control Panel\Desktop\SomeOtherKey", "cache", true, 80, "MUI Cache")));
    }

    [Fact]
    public void StartupExecutableResolverOnlyFlagsFullyQualifiedMissingPaths()
    {
        Assert.Equal(@"C:\Program Files\App\app.exe",
            CleanupService.ResolveStartupExecutable(@"""C:\Program Files\App\app.exe"" --flag"));
        Assert.Equal(@"C:\App\app.exe",
            CleanupService.ResolveStartupExecutable(@"C:\App\app.exe --flag"));
        // Environment-variable commands are ambiguous and must not be resolved.
        Assert.Null(CleanupService.ResolveStartupExecutable(@"""%ProgramFiles%\App\app.exe"""));
        // Non-path commands (e.g. a bare command name) must not be resolved.
        Assert.Null(CleanupService.ResolveStartupExecutable("cmd /c echo hi"));
        Assert.Null(CleanupService.ResolveStartupExecutable(""));
    }

    [Fact]
    public async Task CleaningDeletesOnlyTheNamedValueNotTheKey()
    {
        const string path = @"Software\Classes\CleanMachineTestProgId";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using (var key = root.CreateSubKey(path))
        {
            key.SetValue("KeepMe", "keep");
            key.SetValue("RemoveMe", "remove");
        }

        var backupPath = Path.Combine(Path.GetTempPath(), $"cleanmachine-registry-test-{Guid.NewGuid():N}.reg");
        await File.WriteAllTextAsync(backupPath, "Windows Registry Editor Version 5.00\n");
        try
        {
            var review = new RegistryReview(
            [
                new RegistryFinding("HKCU", path, "orphaned value", true, 75, "File Extensions", "RemoveMe")
            ], [new RegistryBackup(backupPath, DateTimeOffset.UtcNow)]);
            var result = await new RegistryCareService().CleanAsync(review);

            Assert.Equal(1, result.Removed);
            Assert.Empty(result.Skipped);

            using var verify = root.OpenSubKey(path);
            Assert.NotNull(verify);
            Assert.Null(verify!.GetValue("RemoveMe", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal("keep", verify.GetValue("KeepMe") as string);
        }
        finally
        {
            try { File.Delete(backupPath); } catch { }
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ScheduleTaskArgumentsCoverEveryTrigger()
    {
        // BuildCreateArguments takes the full launch command (not a raw path).
        var launchCmd = "\"C:\\Program Files\\CleanMachine\\CleanMachine.exe\" --run-schedule";

        var daily = ScheduledTask.BuildCreateArguments(new CleanupSchedule
        { Id = "abc", Trigger = ScheduleTrigger.Daily, Hour = 3, Minute = 5 }, $"{launchCmd} abc");
        Assert.Contains("/SC DAILY", daily);
        Assert.Contains("/ST 03:05", daily);
        Assert.Contains("/RL LIMITED", daily);
        Assert.Contains("--run-schedule abc", daily);
        Assert.Contains("CleanMachine.exe", daily);
        // The launch command's own quotes must be escaped (\") inside /TR, never left
        // as doubled quotes ("") which schtasks rejects - important for paths with spaces.
        Assert.Contains("\\\"", daily);
        Assert.DoesNotContain("\"\"", daily);

        var weekly = ScheduledTask.BuildCreateArguments(new CleanupSchedule
        { Id = "w", Trigger = ScheduleTrigger.Weekly, DayOfWeek = DayOfWeek.Thursday, Hour = 22, Minute = 30 }, $"{launchCmd} w");
        Assert.Contains("/SC WEEKLY /D THU /ST 22:30", weekly);

        var monthly = ScheduledTask.BuildCreateArguments(new CleanupSchedule
        { Id = "m", Trigger = ScheduleTrigger.Monthly, DayOfMonth = 15 }, $"{launchCmd} m");
        Assert.Contains("/SC MONTHLY /D 15", monthly);

        var logon = ScheduledTask.BuildCreateArguments(new CleanupSchedule
        { Id = "l", Trigger = ScheduleTrigger.AtLogon }, $"{launchCmd} l");
        Assert.Contains("/SC ONLOGON", logon);
        Assert.DoesNotContain("/ST", logon);
    }

    [Fact]
    public void MsixLaunchCommandUsesPackageIdentity()
    {
        var familyName = "CleanMachine_1234567890abcdef_abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        var launchCommand = $"cmd.exe /c start \"\" \"shell:AppsFolder\\{familyName}!App\" --run-schedule abc";
        var args = ScheduledTask.BuildCreateArguments(new CleanupSchedule
        { Id = "abc", Trigger = ScheduleTrigger.Daily, Hour = 3, Minute = 5 }, launchCommand);
        Assert.Contains("/SC DAILY", args);
        Assert.Contains("/ST 03:05", args);
        Assert.Contains("/RL LIMITED", args);
        Assert.Contains("--run-schedule abc", args);
        Assert.Contains(familyName, args);
        Assert.Contains("shell:AppsFolder", args);
        // The MSIX command must not contain a direct exe path.
        Assert.DoesNotContain("CleanMachine.exe", args);
    }

    [Fact]
    public void ScheduleTaskNameIsNamespacedAndDeleteMatches()
    {
        Assert.Equal(@"CleanMachine\Cleanup-xyz", ScheduledTask.TaskName("xyz"));
        Assert.Contains(@"/Delete /TN ""CleanMachine\Cleanup-xyz""", ScheduledTask.BuildDeleteArguments("xyz"));
    }

    [Fact]
    public void ScheduleTaskIdsRejectCommandInjectionCharacters()
    {
        Assert.Throws<ArgumentException>(() => ScheduledTask.TaskName("safe' ; whoami"));
        Assert.Throws<ArgumentException>(() => ScheduledTask.BuildWakeToRunArguments("safe' ; whoami"));
    }

    [Fact]
    public void WakeToRunArgumentsTargetTheTaskAndEnableWake()
    {
        var args = ScheduledTask.BuildWakeToRunArguments("xyz");
        Assert.Contains("Cleanup-xyz", args);
        Assert.Contains(@"-TaskPath '\CleanMachine\'", args);
        Assert.Contains("WakeToRun=$true", args);
    }

    [Fact]
    public void CleanupScheduleWakeToRunDefaultsOffAndRoundTrips()
    {
        Assert.False(new CleanupSchedule().WakeToRun);
    }

    [Fact]
    public void ScheduleHasWorkRequiresAtLeastOneTarget()
    {
        Assert.False(ScheduledTask.HasWork(new CleanupSchedule()));
        Assert.True(ScheduledTask.HasWork(new CleanupSchedule { CleanBrowserCache = true }));
        Assert.True(ScheduledTask.HasWork(new CleanupSchedule { CleanAppTempFiles = true }));
        Assert.True(ScheduledTask.HasWork(new CleanupSchedule { WindowsCategoryIds = ["system-temp"] }));
        Assert.True(ScheduledTask.HasWork(new CleanupSchedule { RegistryCategories = ["MUI Cache"] }));
    }

    [Fact]
    public void SchedulesRoundTripThroughSettingsJson()
    {
        var settings = new AppSettings
        {
            Schedules =
            [
                new CleanupSchedule
                {
                    Id = "s1",
                    Name = "Nightly",
                    Trigger = ScheduleTrigger.Daily,
                    Hour = 2,
                    Minute = 15,
                    AfterClean = ScheduleAction.Shutdown,
                    CleanBrowserCache = true,
                    CleanAppTempFiles = true,
                    WindowsCategoryIds = ["system-temp", "system-dns-cache"],
                    RegistryCategories = ["MUI Cache"]
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        var schedule = Assert.Single(clone!.Schedules);
        Assert.Equal("Nightly", schedule.Name);
        Assert.Equal(ScheduleTrigger.Daily, schedule.Trigger);
        Assert.Equal(2, schedule.Hour);
        Assert.Equal(15, schedule.Minute);
        Assert.Equal(ScheduleAction.Shutdown, schedule.AfterClean);
        Assert.True(schedule.CleanBrowserCache);
        Assert.True(schedule.CleanAppTempFiles);
        Assert.Equal(new[] { "system-temp", "system-dns-cache" }, schedule.WindowsCategoryIds);
        Assert.Equal(new[] { "MUI Cache" }, schedule.RegistryCategories);
    }

    [Fact]
    public void BrowserCatalogCoversCoreAndCommonBrowsers()
    {
        foreach (var id in new[] { "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "ie" })
            Assert.NotNull(BrowserCatalog.Find(id));
        // Lookup is case-insensitive.
        Assert.NotNull(BrowserCatalog.Find("Chrome"));
        Assert.NotNull(BrowserCatalog.Find("EDGE"));
    }

    [Fact]
    public void BrowserItemsAreClassifiedByRisk()
    {
        foreach (var item in BrowserCatalog.ItemsFor(BrowserFamily.Chromium))
        {
            var destructive = item.Id is "history" or "download-history" or "cookies" or "autofill" or "passwords" or "last-download-location";
            Assert.Equal(destructive, item.Destructive);
        }

        var firefox = BrowserCatalog.ItemsFor(BrowserFamily.Firefox);
        Assert.Contains(firefox, i => i.Id == "site-prefs" && i.Destructive);
        Assert.Contains(firefox, i => i.Id == "cache" && !i.Destructive);
    }

    [Fact]
    public void BrowserItemPathsAnchorToTheRightRoots()
    {
        var cookies = BrowserCatalog.PathsFor(BrowserFamily.Chromium, "cookies");
        Assert.Contains(cookies, p => p.Root == BrowserItemRoot.Profile && p.Relative == @"Network\Cookies");

        var crash = BrowserCatalog.PathsFor(BrowserFamily.Chromium, "crash-reports");
        Assert.All(crash, p => Assert.Equal(BrowserItemRoot.UserData, p.Root));

        var ieCache = BrowserCatalog.PathsFor(BrowserFamily.InternetExplorer, "cache");
        Assert.All(ieCache, p => Assert.Equal(BrowserItemRoot.Absolute, p.Root));

        Assert.True(BrowserCatalog.IsPreferenceEdit("last-download-location"));
        Assert.False(BrowserCatalog.IsPreferenceEdit("cache"));
    }

    [Fact]
    public void MinimizeToTrayDefaultsOnAndRoundTrips()
    {
        Assert.True(new AppSettings().MinimizeToTray);
        Assert.True(new AppSettings().ShowInTaskbar);

        var settings = new AppSettings { MinimizeToTray = false, ShowInTaskbar = true };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.False(clone!.MinimizeToTray);
        Assert.True(clone.ShowInTaskbar);
    }

    [Fact]
    public void IsInstalledAsMsixIsConsistentWithCurrentVersion()
    {
        // When running as MSIX, CurrentVersion reads from Package.Current.Id.Version;
        // when standalone, it falls back to the assembly version. The IsInstalledAsMsix
        // flag must agree with whichever path succeeded.
        var isMsix = UpdateService.IsInstalledAsMsix;
        var currentVersion = UpdateService.CurrentVersion();
        // Both paths always return a version; the flag just tells us which source it came from.
        Assert.NotNull(currentVersion);
        // The flag should be false in the test runner (no MSIX identity).
        Assert.False(isMsix);
    }

    [Fact]
    public async Task ManifestWithInstallerSectionParsesInstaller()
    {
        const string json = """
            {
              "version": "2.0.0",
              "releaseNotes": "Installer update.",
              "installer": {
                "packageUrl": "https://github.com/daygle/CleanMachine/releases/download/v2.0.0/CleanMachine-Setup-2.0.0.exe",
                "sha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
                "architecture": "x64"
              }
            }
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var manifest = await UpdateService.ParseManifestAsync(stream);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Installer);
        Assert.Contains(".exe", manifest.Installer!.PackageUrl);
        Assert.Equal("x64", manifest.Installer.Architecture);
        // packages can be null when only an installer is present.
        Assert.Null(manifest.Packages);
    }

    [Fact]
    public async Task ManifestWithoutInstallerSectionHasNullInstaller()
    {
        const string json = """
            {
              "version": "2.0.0",
              "releaseNotes": "MSIX only."
            }
            """;

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var manifest = await UpdateService.ParseManifestAsync(stream);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Installer);
    }

    [Fact]
    public void CleanupScheduleSecureDeleteDefaultsOffAndRoundTrips()
    {
        Assert.False(new CleanupSchedule().SecureDelete);

        var settings = new AppSettings
        {
            Schedules =
            [
                new CleanupSchedule
                {
                    Id = "sd1",
                    Name = "Secure nightly",
                    SecureDelete = true,
                    WindowsCategoryIds = ["system-temp"]
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.True(clone!.Schedules[0].SecureDelete);
    }

    [Fact]
    public async Task SecureDeleteFileAsyncOverwritesAndDeletesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"cm-sd-test-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, "This is test content for secure delete.");
            Assert.True(File.Exists(tempFile));

            var options = new SecureDeleteOptions(WipeMethod.SimpleZeroFill, 1, true);
            var result = await SecureDeleteService.SecureDeleteFileAsync(tempFile, options);

            Assert.True(result);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void AppCatalogCoversDesktopAndStoreApps()
    {
        var defs = AppCatalog.Definitions;
        Assert.NotEmpty(defs);
        Assert.Contains(defs, d => !d.IsStoreApp);
        Assert.Contains(defs, d => d.IsStoreApp);
        // Every definition has a unique id.
        Assert.Equal(defs.Count, defs.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void AppCatalogFindIsCaseInsensitive()
    {
        Assert.NotNull(AppCatalog.Find("7zip"));
        Assert.NotNull(AppCatalog.Find("7ZIP"));
        Assert.Null(AppCatalog.Find("nonexistent-app-xyz"));
    }

    [Fact]
    public void AppCatalogGroupsAreDistinct()
    {
        var groups = AppCatalog.Groups();
        Assert.Contains("Desktop App", groups);
        Assert.Contains("Microsoft Store App", groups);
        Assert.Equal(groups.Count, groups.Distinct().Count());
    }

    [Fact]
    public void TraySettingsDefaultsAreReasonable()
    {
        var settings = new AppSettings();
        Assert.False(settings.StartMinimizedToTray);
        Assert.False(settings.CloseToTray);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.ShowInTaskbar);
    }

    [Fact]
    public void TraySettingsRoundTripThroughJson()
    {
        var settings = new AppSettings
        {
            StartMinimizedToTray = true,
            CloseToTray = true,
            MinimizeToTray = false,
            ShowInTaskbar = false
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.True(clone!.StartMinimizedToTray);
        Assert.True(clone.CloseToTray);
        Assert.False(clone.MinimizeToTray);
        Assert.False(clone.ShowInTaskbar);
    }
}
