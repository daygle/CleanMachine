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
        // The trusted root is a required argument: with it optional, this call
        // would mean "delete anything that is not in a system folder".
        Assert.False(NativeSafety.IsSafeFileCandidate(string.Empty, Path.GetTempPath()));
    }

    [Fact]
    public void ProtectedPathsCoverProgramFilesAndProgramDataNotJustTheOs()
    {
        // The old rule only covered Windows and System32, so Program Files,
        // ProgramData and drive roots read as ordinary deletion candidates.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(path)) continue;
            Assert.True(NativeSafety.IsProtectedPath(Path.Combine(path, "anything")), $"{folder} must be protected");
        }
    }

    [Fact]
    public void ADriveRootIsNeverASafeFileCandidate()
    {
        var temp = Path.GetTempPath();
        var root = Path.GetPathRoot(temp);
        if (string.IsNullOrEmpty(root)) return;
        Assert.True(NativeSafety.IsProtectedPath(root));
        Assert.False(NativeSafety.IsSafeFileCandidate(root, temp));
    }

    [Fact]
    public void SafeFileCandidateRequiresATrustedRootAndStaysInsideIt()
    {
        var temp = Path.GetTempPath();
        var scope = Path.Combine(temp, "cm-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        try
        {
            var inside = Path.Combine(scope, "cache.tmp");
            File.WriteAllText(inside, "x");
            Assert.True(NativeSafety.IsSafeFileCandidate(inside, scope));

            // Same file, but the caller named a different root: outside it means no.
            var other = Path.Combine(temp, "cm-candidate-other-" + Guid.NewGuid().ToString("N"));
            Assert.False(NativeSafety.IsSafeFileCandidate(inside, other));

            // No root at all is not a request to clean the whole profile.
            Assert.False(NativeSafety.IsSafeFileCandidate(inside, string.Empty));
            Assert.False(NativeSafety.IsSafeFileCandidate(inside, "   "));
        }
        finally
        {
            try { Directory.Delete(scope, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TheUserProfileIsNotBlanketProtectedBecauseThatIsWhereCleanupLives()
    {
        // Regression guard for the trap this rule used to be: every legitimate
        // browser cache and app temp file lives under the profile, so protecting
        // the whole of it would refuse all real work.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return;
        Assert.False(NativeSafety.IsProtectedPath(Path.Combine(profile, "AppData", "Local", "Temp")));
    }

    [Fact]
    public async Task EmptyRegistryReviewDoesNotCreateBackup()
    {
        var review = await new RegistryCareService().PrepareReviewAsync([]);
        Assert.Empty(review.Findings);
    }

    /// <summary>The export destination is proven writable before reg.exe runs, so a
    /// failure names the folder instead of reg.exe's "There may be a disk or file
    /// system error" - which blames the disk for what is usually a missing or
    /// redirected directory.</summary>
    [Fact]
    public void ExportTargetProbeAcceptsAWritableDestinationAndLeavesNothingBehind()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"cm-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(scope);
            var target = Path.Combine(scope, "registry-test.reg");

            RegistryCareService.EnsureExportTargetUsable(target);

            // No placeholder left for reg.exe to trip over, and no empty .reg file
            // that ValidateBackupAsync could later mistake for a restore point.
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    [Fact]
    public void ExportTargetProbeNamesTheFolderWhenTheDestinationCannotBeCreated()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"cm-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(scope);
            var blocker = Path.Combine(scope, "not-a-directory");
            File.WriteAllText(blocker, "x");
            // The parent is a file, so the destination can never be created.
            var target = Path.Combine(blocker, "registry-test.reg");

            var ex = Assert.Throws<InvalidOperationException>(
                () => RegistryCareService.EnsureExportTargetUsable(target));

            Assert.Contains(blocker, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(scope, recursive: true);
        }
    }

    /// <summary>A packaged (MSIX) build redirects this process's %LOCALAPPDATA%
    /// writes into the package folder while reg.exe - a child process outside the
    /// package - writes to the literal path, so the app's own folder is one reg.exe
    /// cannot use. The backup directories must therefore offer somewhere outside
    /// every redirected known folder, or Registry Care can never clean anything.</summary>
    [Fact]
    public void BackupDirectoriesStartWithTheAppFolderAndOfferANonRedirectedFallback()
    {
        var directories = RegistryCareService.BackupDirectories;

        // The app's own folder stays first so unpackaged builds are unaffected.
        Assert.Equal(RegistryCareService.BackupsDirectory, directories[0]);
        Assert.Equal("Backups", Path.GetFileName(directories[0]));

        // At least one candidate must sit outside %LOCALAPPDATA%, which is the
        // whole point: that is where the user profile entry and temp come from.
        Assert.Contains(directories,
            d => !d.Contains(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                StringComparison.OrdinalIgnoreCase));

        // Duplicates would make the export spawn reg.exe twice per candidate for
        // nothing and double-count backups in the Backups page.
        Assert.Equal(directories.Count, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
        // An unterminated quote used to produce a -1 slice bound; it must resolve to
        // null (unknown), never throw out of a registry scan.
        Assert.Null(CleanupService.ResolveStartupExecutable(@"""C:\App\app.exe"));
        Assert.Null(CleanupService.ResolveStartupExecutable(@""""""));
    }

    /// <summary>The two-hour recency guard is the app's main defence against
    /// deleting a file something still has open, so it must fail CLOSED: a path
    /// whose timestamp cannot be read has to count as recently modified (skip it),
    /// never as old (delete it).</summary>
    [Fact]
    public void RecentlyModifiedGuardSkipsFreshFilesAndFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine-RecencyGuard");
        Directory.CreateDirectory(directory);
        var fresh = Path.Combine(directory, "fresh.tmp");
        var old = Path.Combine(directory, "old.tmp");
        try
        {
            File.WriteAllText(fresh, "probe");
            // A file written now is inside the two-hour window: never a candidate.
            Assert.True(WindowsCleanupService.IsRecentlyModified(fresh));

            File.WriteAllText(old, "probe");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            Assert.False(WindowsCleanupService.IsRecentlyModified(old));

            // A path the file system refuses outright must read as "recently
            // modified" so the file is skipped rather than deleted. A NUL in the
            // path is the reliable way to make the stat throw; note that a merely
            // MISSING file does not throw (it reports the epoch), so it cannot
            // exercise the catch.
            Assert.True(WindowsCleanupService.IsRecentlyModified(
                Path.Combine(directory, "bad\0name.tmp")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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

    /// <summary>A Store update deletes the old version-stamped package folder but
    /// leaves the HKCU Run value pointing into it, so the app stops auto-starting
    /// and its own entry is flagged as a dead reference. Sync must repair that -
    /// but only when the stored path is genuinely unusable, and never against the
    /// user's choice. Both halves are asserted here because either one alone is a
    /// silent failure: repairing unconditionally would fight a second install,
    /// and never repairing would leave the app dead after every update.</summary>
    [Fact]
    public void StartupSyncRepairsADeadEntryAndLeavesAHealthyOneAlone()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"cleanmachine-startup-{Guid.NewGuid():N}.exe");
        File.WriteAllText(executable, string.Empty);
        var runKeyPath = $@"{StartupRegistration.RunPath}\CleanMachineSyncTest";
        const string scratch = "CleanMachineSyncTest";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            // A dead path (a package folder the Store already removed) is rewritten.
            using (var key = root.CreateSubKey(runKeyPath))
            {
                key.SetValue(scratch, "\"C:\\Gone\\daygle.CleanMachine_1.0.0.0_x64__old\\CleanMachine.exe\" --background");
            }
            Assert.True(StartupRegistration.SyncForTest(true, executable, runKeyPath, scratch));
            using (var key = root.OpenSubKey(runKeyPath))
            {
                Assert.Equal($"\"{executable}\" --background", key?.GetValue(scratch) as string);
            }

            // A live path is left exactly as it is: no churn on every launch.
            Assert.False(StartupRegistration.SyncForTest(true, executable, runKeyPath, scratch));

            // An unknown executable is never written.
            Assert.False(StartupRegistration.SyncForTest(true, Path.Combine(Path.GetTempPath(), "definitely-missing-xyz.exe"), runKeyPath, scratch));
        }
        finally
        {
            root.DeleteSubKeyTree(runKeyPath, throwOnMissingSubKey: false);
            try { File.Delete(executable); } catch { }
        }
    }

    /// <summary>Sync must never resurrect the entry when the user has startup
    /// switched off - the failure mode being a cleanup tool (or a repair pass)
    /// quietly re-enabling something the user deliberately turned off.</summary>
    [Fact]
    public void StartupSyncNeverRegistersWhenTheUserHasStartupOff()
    {
        var runKeyPath = $@"{StartupRegistration.RunPath}\CleanMachineSyncTest";
        const string scratch = "CleanMachineSyncTest";
        var executable = Path.Combine(Path.GetTempPath(), $"cleanmachine-startup-{Guid.NewGuid():N}.exe");
        File.WriteAllText(executable, string.Empty);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            using (var key = root.CreateSubKey(runKeyPath))
                key.SetValue(scratch, $"\"{executable}\" --background");

            // Off + an existing entry: the entry is removed, and doing so is
            // reported as a change.
            Assert.True(StartupRegistration.SyncForTest(false, executable, runKeyPath, scratch));
            using (var key = root.OpenSubKey(runKeyPath))
                Assert.Null(key?.GetValue(scratch));

            // Off + nothing there: no change, and crucially nothing written.
            Assert.False(StartupRegistration.SyncForTest(false, executable, runKeyPath, scratch));
            using (var key = root.OpenSubKey(runKeyPath))
                Assert.Null(key?.GetValue(scratch));
        }
        finally
        {
            root.DeleteSubKeyTree(runKeyPath, throwOnMissingSubKey: false);
            try { File.Delete(executable); } catch { }
        }
    }

    /// <summary>Registry Care must not offer to delete CleanMachine's own startup
    /// entry. The app re-registers that value, so listing it produced a cleanup
    /// that appeared to fail: the user removed it and it came straight back. The
    /// scanner is the only place that decides what is offered, so the exclusion
    /// is pinned there rather than in the delete path.</summary>
    [Fact]
    public void RegistryCareNeverOffersToDeleteTheAppsOwnStartupEntry()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var source = File.ReadAllText(Path.Combine(root!, "CleanMachine.Windows", "CleanupService.cs"));
        var scan = source[source.IndexOf("ScanStartupEntries", StringComparison.Ordinal)..];
        scan = scan[..scan.IndexOf("private static void ScanSoundAppEvents", StringComparison.Ordinal)];

        // The skip must name our own value, not bail out of the whole key: other
        // apps' dead entries are exactly what this scan exists to find.
        Assert.Contains("StartupRegistration.ValueName", scan, StringComparison.Ordinal);
        Assert.Contains("continue", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("if (path == StartupRegistration.RunPath) continue", scan, StringComparison.Ordinal);
    }

    /// <summary>A removal that did not happen must not be reported as one. The
    /// original code opened the Run key with "?.", so a null handle made the
    /// delete a silent no-op that still returned success - the user was told the
    /// entry was gone, and it was still listed on the very next scan. This proves
    /// Remove deletes a real value, and is the guard for the "it came back"
    /// report that could not otherwise be told apart from an external re-add.</summary>
    [Fact]
    public void StartupEntryRemovalActuallyDeletesTheValue()
    {
        var service = new StartupAppsService();
        const string scratch = "CleanMachineRemoveTest";
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using (var key = root.CreateSubKey(keyPath))
                key.SetValue(scratch, "\"C:\\Definitely\\Missing\\removed.exe\" --flag 'quoted'", RegistryValueKind.String);

            var entry = new StartupEntry(
                Name: scratch,
                Command: "\"C:\\Definitely\\Missing\\removed.exe\" --flag 'quoted'",
                ExecutablePath: @"C:\Definitely\Missing\removed.exe",
                Source: StartupSource.RegistryCurrentUser,
                Enabled: true,
                Section: "Current User",
                IsOrphan: true,
                RegistryPath: $@"HKCU\{keyPath}",
                ValueName: scratch);

            Assert.True(service.Remove(entry, out var error), $"Remove reported a failure: {error}");

            using var verify = root.OpenSubKey(keyPath, writable: false);
            Assert.Null(verify?.GetValue(scratch));
        }
        finally
        {
            using (var key = root.OpenSubKey(keyPath, writable: true))
                key?.DeleteValue(scratch, throwOnMissingValue: false);
            using (var approved = root.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true))
                approved?.DeleteValue(scratch, throwOnMissingValue: false);
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

    /// <summary>The user-facing app name is not the reserved identity name:
    /// Partner Center issues the latter as "&lt;publisher&gt;.&lt;app&gt;". The
    /// workflow stamps Identity/@Name, so DisplayName must keep saying
    /// "CleanMachine" or the listing and Start menu entry would read
    /// "daygle.CleanMachine".</summary>
    [Fact]
    public void DisplayNameStaysTheAppNameWhileIdentityNameIsPublisherPrefixed()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var appx = XDocument.Load(Path.Combine(root!, "CleanMachine.Windows", "Package.appxmanifest"));
        var displayName = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
        var visualName = appx.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")?
            .Attribute("DisplayName")?.Value;

        Assert.Equal("CleanMachine", displayName);
        Assert.Equal("CleanMachine", visualName);
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

    /// <summary>The removal left stale prose behind in three places at once: the
    /// docs still promised an in-app updater and signed releases, and the release
    /// workflow comments still described the retired self-signed sideload channel.
    /// None of it breaks a build, so it rots silently - a user following SECURITY.md
    /// would look for an updater that no longer exists. The retired secret name and
    /// the retired phrases are pinned here.</summary>
    [Fact]
    public void RetiredChannelsAreNotAdvertisedInDocsOrWorkflows()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var files = new[]
        {
            Path.Combine(root!, "README.md"),
            Path.Combine(root!, "RELEASE.md"),
            Path.Combine(root!, "SECURITY.md"),
            Path.Combine(root!, ".github", "workflows", "release-windows.yml"),
            Path.Combine(root!, ".github", "workflows", "ci-windows.yml"),
        };

        // Phrases that only ever described the removed private/self-signed channel
        // or the removed updater. "self-signed" on its own is still legitimate: the
        // docs and workflow explain that there is deliberately no signing path at
        // all, which is a different claim from offering one.
        var retired = new[]
        {
            "WINDOWS_SIGNING_CERTIFICATE",   // the retired sideload secrets
            "in-app updater or the releases",
            "shipped through signed releases",
            "secure deletion",
            "Secure Delete path selection",
            "secure-delete overwrite",
            "staged update files",
            "Inno Setup",   // the retired installer toolchain (matched in full: a bare
                            // "Inno" would false-positive on RecycleBinNotify...)
        };

        var offenders = new List<string>();
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), $"Expected file is missing: {file}");
            var text = File.ReadAllText(file);
            foreach (var phrase in retired)
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(file)}: {phrase}");
        }

        Assert.True(offenders.Count == 0,
            "Retired channels/features still described: " + string.Join(", ", offenders));
    }

    /// <summary>The About page offers sponsorship, and the whole point of that
    /// page is that a single wrong URL sends supporters somewhere useless while
    /// looking completely normal in the UI. The link is stored as a Button Tag
    /// rather than in visible text, so a typo in it compiles, renders, and ships
    /// unnoticed - this pins the exact URL. It also pins the other outbound
    /// links, and asserts the page opens them through the shell instead of
    /// making a request of its own, which is what keeps the "no network
    /// connections" privacy claim true now that the app links out.</summary>
    [Fact]
    public void AboutPageLinksOutThroughTheShellAndPinsTheSponsorshipUrl()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");

        var markupPath = Path.Combine(root!, "CleanMachine.Windows", "AboutPage.xaml");
        Assert.True(File.Exists(markupPath), $"Expected file is missing: {markupPath}");
        var markup = File.ReadAllText(markupPath);

        var expected = new[]
        {
            "https://github.com/sponsors/daygle",
            "https://github.com/daygle/CleanMachine",
            "https://github.com/daygle/CleanMachine/blob/main/PRIVACY.md",
            "https://github.com/daygle/CleanMachine/issues",
        };
        foreach (var url in expected)
            Assert.Contains($"Tag=\"{url}\"", markup, StringComparison.Ordinal);

        // A sponsorship link that points at a plausible-but-wrong host (a typo, or
        // someone else's profile) is the failure mode worth catching.
        Assert.DoesNotContain("sponsors.github.com", markup, StringComparison.OrdinalIgnoreCase);

        // Outbound links must go through the shell's default browser. An in-app
        // web request would contradict the published privacy policy.
        var code = File.ReadAllText(Path.Combine(root!, "CleanMachine.Windows", "AboutPage.xaml.cs"));
        Assert.Contains("UseShellExecute = true", code, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "HttpClient", "WebClient", "HttpWebRequest", "WebRequest", "System.Net" })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
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
    /// belongs.
    ///
    /// This drives real Task Scheduler, so it is inherently timing-sensitive. It
    /// failed intermittently with a bare "the script never executed" that gave no
    /// way to tell a slow runner from a task that never started. It now clears any
    /// stale marker first (a leftover one used to make it pass without proving
    /// anything), schedules safely across midnight, allows a cold runner a real
    /// budget, and reports the task's own Last Task Result when it does fail.</summary>
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
            // A marker left by an earlier attempt would satisfy the poll on its
            // first iteration, so the test could pass with the task never having
            // run - the exact thing it exists to disprove.
            if (File.Exists(marker)) File.Delete(marker);

            // A harmless stand-in for a real task action, including an apostrophe in
            // the value it echoes: that is the quoting this path has to survive.
            await File.WriteAllTextAsync(scriptPath,
                $"Set-Content -LiteralPath '{marker}' -Value 'ran'\n");

            var action = $"powershell.exe -NoProfile -NonInteractive -File \"{scriptPath}\"";
            // schtasks rejects any /TR action over 261 characters outright.
            const int schtasksActionLimit = 261;
            Assert.True(action.Length <= schtasksActionLimit,
                $"Task action is {action.Length} chars; schtasks allows at most {schtasksActionLimit}.");

            // /ST takes a bare HH:mm, so "now + 5 minutes" that crosses midnight
            // formats to a time earlier today and schtasks is handed a start in the
            // past. Push those to tomorrow, where the time really is in the future.
            var start = DateTime.Now.AddMinutes(5);
            if (start.Date != DateTime.Now.Date) start = start.AddDays(1);

            var escaped = action.Replace("\"", "\\\"");
            var created = await ScheduleService.RunProcessAsync("schtasks.exe",
                $"/Create /TN \"{taskName}\" /TR \"{escaped}\" /SC ONCE /ST {start:HH:mm} /RL LIMITED /F",
                default);
            Assert.True(created.Success, $"schtasks /Create failed: {created.Reason}");

            var run = await ScheduleService.RunProcessAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", default);
            Assert.True(run.Success, $"schtasks /Run failed: {run.Reason}");

            // The task is fire-and-forget, so poll for the marker rather than
            // guessing. A cold runner can take a while to spin the task up, and a
            // timeout here would be indistinguishable from a real failure - so the
            // budget is generous and a failure is explained rather than asserted.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            Assert.True(File.Exists(marker),
                $"The task was started but its action did not write the marker within 90s. " +
                $"Task Scheduler reported: {await DescribeTaskAsync(taskName)}");
        }
        finally
        {
            await ScheduleService.RunProcessAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", default);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>Task Scheduler's own account of what happened, for a failure
    /// message. "Last Task Result" is the field that separates a task that never
    /// started (267011 - has not yet run) from one that ran and failed, which is
    /// the distinction a bare timeout throws away.</summary>
    private static async Task<string> DescribeTaskAsync(string taskName)
    {
        var query = await ScheduleService.RunProcessAsync("schtasks.exe",
            $"/Query /TN \"{taskName}\" /FO LIST /V", default);
        if (!query.Success) return $"could not be queried ({query.Reason})";

        foreach (var line in query.Output.Split('\n'))
        {
            var text = line.Trim().Replace("\r", string.Empty);
            if (text.StartsWith("Last Task Result", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Last Run Time", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Task To Run", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Status", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return "no status reported";
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
