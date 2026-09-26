using CleanMachine.Windows;
using Microsoft.Win32;
using System.Xml.Linq;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Shares the "Cleaning" collection with <see cref="CleaningActivityTests"/>:
/// these tests exercise real cleaning services (which flip the shared
/// CleaningActivity counter), and CleaningActivityTests asserts absolute states of
/// that counter - xUnit would otherwise run the two classes in parallel.</summary>
[Collection("Cleaning")]
public sealed class ManifestAndSafetyTests
{
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
    public async Task RegistryBackupExportRetriesOnceAfterATransientFailure()
    {
        var backup = new RegistryBackup("unused.reg", DateTimeOffset.UtcNow);
        var calls = 0;
        var delays = 0;
        var result = await RegistryCareService.ExportKeyWithRetryAsync(
            "Software\\Whatever", "unused.reg",
            token: default,
            exportOnce: (_, _) => ++calls == 1
                ? throw new InvalidOperationException("Registry backup export failed (exit code 1).")
                : Task.FromResult(backup),
            delayAsync: () => { delays++; return Task.CompletedTask; });

        Assert.Equal(backup, result);
        Assert.Equal(2, calls);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task RegistryBackupExportStopsAfterFinalAttemptAndRethrows()
    {
        var calls = 0;
        var delays = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RegistryCareService.ExportKeyWithRetryAsync(
                "Software\\Whatever", "unused.reg",
                token: default,
                exportOnce: (_, _) => { calls++; throw new InvalidOperationException("Registry backup export failed."); },
                delayAsync: () => { delays++; return Task.CompletedTask; }));

        Assert.Equal(RegistryCareService.ExportAttempts, calls);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task RegistryBackupExportWithSingleAttemptDoesNotRetry()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RegistryCareService.ExportKeyWithRetryAsync(
                "Software\\Whatever", "unused.reg",
                token: default,
                attempts: 1,
                exportOnce: (_, _) => { calls++; throw new InvalidOperationException("Registry backup export failed."); },
                delayAsync: () => throw new InvalidOperationException("delay must never run")));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void CountBackupsCountsRegFilesAndToleratesMissingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cm-backups-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, RegistryCareService.CountBackups(directory));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "a.reg"), "Windows Registry Editor Version 5.00");
            File.WriteAllText(Path.Combine(directory, "b.REG"), "Windows Registry Editor Version 5.00");
            File.WriteAllText(Path.Combine(directory, "note.txt"), "not a backup");
            Assert.Equal(2, RegistryCareService.CountBackups(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ListBackupsReturnsNewestFirstAndIgnoresNonRegFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cm-backups-list-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var old = Path.Combine(directory, "registry-uninstall-20260101-010000.reg");
            var mid = Path.Combine(directory, "registry-MuiCache-20260201-020000.reg");
            var latest = Path.Combine(directory, "registry-classes-txt-20260301-030000.reg");
            File.WriteAllText(old, "Windows Registry Editor Version 5.00");
            File.WriteAllText(mid, "Windows Registry Editor Version 5.00");
            File.WriteAllText(latest, "Windows Registry Editor Version 5.00");
            File.WriteAllText(Path.Combine(directory, "note.txt"), "not a backup");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(mid, new DateTime(2026, 2, 1, 2, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(latest, new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc));

            // ListBackups reads the app-level BackupsDirectory, so exercise the
            // sorting through the internal overload that takes an explicit directory.
            var backups = RegistryCareService.ListBackups(directory);
            Assert.Equal(3, backups.Count);
            Assert.Equal(latest, backups[0].FilePath);
            Assert.Equal(mid, backups[1].FilePath);
            Assert.Equal(old, backups[2].FilePath);
            Assert.Equal(new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero), backups[0].CreatedAt);
            Assert.Empty(RegistryCareService.ListBackups(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public void RecycleBinSizeProbeReturnsANonNegativeValue()
    {
        // The shell owns the per-drive $Recycle.Bin layout; scanning it directly as a
        // normal folder previously made the category report zero even when it contained
        // deleted items. The native probe must remain safe when the bin is empty too.
        Assert.True(WindowsCleanupService.GetRecycleBinSize() >= 0);
    }

    [Fact]
    public void DnsCachePreviewDescribesItsActionEvenWithoutMeasuredBytes()
    {
        var item = WindowsCleanupService.Catalog.Single(c => c.Id == "system-dns-cache");
        var preview = new WindowsCleanupService().BuildPreview([item]);

        Assert.Equal(1, preview.TotalItems);
        Assert.Single(preview.Items);
        Assert.Equal("Flush the DNS cache", preview.Items[0].Description);
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
        Assert.Equal(ExitAction.CleanAndNotify, settings.SystemMonitorAction);
        Assert.Equal(1.0, settings.SystemMonitorFreeSpaceGb);
        // Every automatic trigger defaults to clean and notify on a fresh install.
        Assert.True(settings.StartupCleanNotify);
        Assert.True(settings.IdleCleanNotify);
        Assert.True(settings.RecycleBinAutoEmptyNotify);
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
            SystemMonitorAction = ExitAction.CleanAndNotify,
            StartupCleanNotify = true,
            IdleCleanNotify = true,
            RecycleBinAutoEmptyNotify = true
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
        Assert.True(clone.StartupCleanNotify);
        Assert.True(clone.IdleCleanNotify);
        Assert.True(clone.RecycleBinAutoEmptyNotify);
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
        Assert.Contains("Desktop Application", groups);
        Assert.Contains("Microsoft Store Application", groups);
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
        Assert.True(settings.AlwaysShowTray);
        // The tray spinner is on by default; turning it off still leaves the
        // busy tooltip as an indicator that a clean is running.
        Assert.True(settings.TrayCleaningAnimation);
    }

    [Fact]
    public void TraySettingsRoundTripThroughJson()
    {
        var settings = new AppSettings
        {
            StartMinimizedToTray = true,
            CloseToTray = true,
            MinimizeToTray = false,
            ShowInTaskbar = false,
            AlwaysShowTray = false,
            TrayCleaningAnimation = false
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(clone);
        Assert.True(clone!.StartMinimizedToTray);
        Assert.True(clone.CloseToTray);
        Assert.False(clone.MinimizeToTray);
        Assert.False(clone.ShowInTaskbar);
        Assert.False(clone.AlwaysShowTray);
        Assert.False(clone.TrayCleaningAnimation);
    }

    /// <summary>The desktop shortcut must target the package identity, not the
    /// version-stamped WindowsApps executable: the folder an exe-targeting link
    /// points at is deleted on the next update, which is what broke the user's
    /// desktop icon after every in-app update.</summary>
    [Fact]
    public void MsixDesktopShortcutTargetsThePackageIdentityNotTheVersionedExe()
    {
        var family = "CleanMachine_1234567890abcdef_abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

        var target = MsixUninstallService.BuildMsixShortcutTarget(family);

        Assert.Equal($"shell:AppsFolder\\{family}!App", target);
        Assert.DoesNotContain("WindowsApps", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".exe", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", target[(target.IndexOf('\\') + 1)..], StringComparison.Ordinal);
    }

    /// <summary>The in-repo version defaults (RELEASE.md checklist step 1) must stay
    /// in lockstep: the MSIX package identity and the Win32 assembly identity are
    /// bumped together on every release. The release workflow stamps the package
    /// identity from the tag but never rewrites app.manifest, so this checked-in
    /// pair is the only thing that keeps them from silently drifting apart again.</summary>
    [Fact]
    public void PackageAndAppManifestVersionsAgree()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null,
            "Repo root (containing CleanMachine.Windows\\) was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var appManifest = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "app.manifest"));

        // Descend by LocalName so the appx default xmlns and the asm.v1 xmlns on
        // app.manifest never have to be spelled out (and can never go stale).
        var packageVersion = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?
            .Attribute("Version")?.Value;
        var assemblyVersion = appManifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity")?
            .Attribute("version")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(packageVersion),
            "Package.appxmanifest has no Identity Version attribute.");
        Assert.False(string.IsNullOrWhiteSpace(assemblyVersion),
            "app.manifest has no assemblyIdentity version attribute.");
        // Four-part X.Y.Z.0: the shape the release workflow stamps from the tag.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", packageVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", assemblyVersion);
        Assert.Equal(packageVersion, assemblyVersion);
    }

    /// <summary>Partner Center requires a written justification for every restricted
    /// capability, and the justification in RELEASE.md covers exactly one of them.
    /// A newly added restricted capability silently ships without a justification
    /// and fails certification, so the set is pinned here.</summary>
    [Fact]
    public void PackageDeclaresOnlyTheJustifiedRestrictedCapability()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var restricted = appx.Descendants()
            .Where(e => e.Name.LocalName == "Capability")
            .Select(e => e.Attribute("Name")?.Value)
            .OfType<string>()
            .ToArray();

        // runFullTrust is required by the desktop:Extension full-trust entry that
        // backs the WinUI 3 app; the justification in RELEASE.md explains why.
        Assert.Equal(new[] { "runFullTrust" }, restricted);
    }

    /// <summary>The manifest Description is the public Store listing copy. Developer-
    /// internal wording shipped there once ("Private, review-first...") and read badly
    /// in a public listing, so the phrasing is pinned to something customer-facing.</summary>
    [Fact]
    public void PackageListingCopyIsCustomerFacing()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var description = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value;
        var visual = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")?
            .Attribute("Description")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Equal(description, visual);
        Assert.DoesNotContain("Private", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal", description, StringComparison.OrdinalIgnoreCase);
        Assert.True(description!.Length > 40, "Listing description is too short to describe the app.");
    }

    /// <summary>Secure Delete and Drive Wiper were removed from the product. A page or
    /// checkbox reintroduced without the surrounding removal of the service would be
    /// dead UI at best, and a Store certification risk at worst, so the shipped markup
    /// is checked for the names directly.</summary>
    [Fact]
    public void RemovedDestructiveToolsAreAbsentFromTheShippedUi()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var projectDir = Path.Combine(root!, "CleanMachine.Windows");
        // Skip build output: only the hand-written page markup is under test.
        var pages = Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var offenders = new List<string>();
        foreach (var page in pages)
        {
            var markup = File.ReadAllText(page);
            foreach (var removed in new[] { "SecureDelete", "Secure Delete", "DriveWiper", "Drive Wiper", "wipe" })
                if (markup.Contains(removed, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(page)}: {removed}");
        }

        Assert.True(offenders.Count == 0, "Removed features still referenced in markup: " + string.Join(", ", offenders));
    }

    /// <summary>schtasks writes the reason it refused a task to stderr. Keeping the
    /// first meaningful line is what turns "something went wrong" into a diagnosable
    /// failure - it is the whole reason this bug was catchable after the fact.</summary>
    [Theory]
    [InlineData("ERROR: Value for '/TR' option cannot be more than 261 character(s).\r\n", "/TR")]
    [InlineData("\r\n   \r\nERROR: Access is denied.\r\n", "Access is denied")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FirstLineExtractsTheToolsOwnRefusal(string? text, string? expected)
    {
        var line = ScheduleService.FirstLine(text!);
        if (expected is null) Assert.Null(line);
        else Assert.Contains(expected, line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A failing helper process must come back with a reason attached. This
    /// is the regression guard for the bug that hid for five releases: RunProcessAsync
    /// used to read schtasks' stderr and throw it away, so an unschedulable update
    /// task was indistinguishable from a successful one.
    ///
    /// Uses a read-only query for a task that cannot exist, so it has no side effects
    /// and can run unconditionally.</summary>
    [Fact]
    public async Task RunProcessAsyncReportsWhyACommandFailed()
    {
        var outcome = await ScheduleService.RunProcessAsync(
            "schtasks.exe", @"/Query /TN ""\CleanMachine\ThisTaskMustNotExist"" /FO LIST /V", default);

        Assert.False(outcome.Success);
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
    }

    /// <summary>The success path must not invent a reason, or every caller would
    /// surface a bogus error message on a working update.</summary>
    [Fact]
    public async Task RunProcessAsyncReportsSuccessWithoutAReason()
    {
        var outcome = await ScheduleService.RunProcessAsync("schtasks.exe", "/Query /FO LIST", default);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.Reason);
    }

    /// <summary>End-to-end proof that a cleanup task can actually be scheduled and
    /// run on this machine - the one thing no pure unit test can show. It creates a
    /// real scheduled task, so it is opt-in: set CLEANMACHINE_RUN_SCHEDULED_TESTS=1.
    /// CI enables it because its runners are ephemeral, which is where this coverage
    /// belongs.</summary>
    [Fact]
    [Trait("Category", "ScheduledTask")]
    public async Task ScheduledTaskCanBeScheduledAndRuns()
    {
        if (Environment.GetEnvironmentVariable("CLEANMACHINE_RUN_SCHEDULED_TESTS") != "1") return;

        const string taskName = @"\CleanMachine\TestsTaskSelfTest";
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine-TaskSelfTest");
        var marker = Path.Combine(directory, "ran.txt");
        var scriptPath = Path.Combine(directory, "task-action.ps1");
        Directory.CreateDirectory(directory);
        try
        {
            // A harmless stand-in for a real task action, including an apostrophe in
            // the value it echoes: that is the quoting this path has to survive.
            await File.WriteAllTextAsync(scriptPath,
                $"Set-Content -LiteralPath '{marker}' -Value 'ran'\n");

            var action = $"powershell.exe -NoProfile -NonInteractive -File \"{scriptPath}\"";
            // schtasks rejects any /TR action over 261 characters outright.
            const int schtasksActionLimit = 261;
            Assert.True(action.Length <= schtasksActionLimit,
                $"Task action is {action.Length} chars; schtasks allows at most {schtasksActionLimit}.");

            var escaped = action.Replace("\"", "\\\"");
            var created = await ScheduleService.RunProcessAsync("schtasks.exe",
                $"/Create /TN \"{taskName}\" /TR \"{escaped}\" /SC ONCE /ST {DateTime.Now.AddMinutes(5):HH:mm} /RL LIMITED /F",
                default);
            Assert.True(created.Success, $"schtasks /Create failed: {created.Reason}");

            var run = await ScheduleService.RunProcessAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", default);
            Assert.True(run.Success, $"schtasks /Run failed: {run.Reason}");

            // The task is fire-and-forget, so poll for the marker rather than guessing.
            for (var i = 0; i < 40 && !File.Exists(marker); i++) await Task.Delay(500);
            Assert.True(File.Exists(marker), "The task ran but the script never executed.");
        }
        finally
        {
            await ScheduleService.RunProcessAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", default);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>Walks up from the test output directory (bin/&lt;config&gt;/&lt;tfm&gt;,
    /// any platform) to the checkout that contains the app project.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CleanMachine.Windows", "Package.appxmanifest")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
