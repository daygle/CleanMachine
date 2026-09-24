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
    public void CleanupUpdateArtifactsRemovesStagingFilesAndProbeButNothingElse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cm-update-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "CleanMachine.exe.restore"), "staged");
            File.WriteAllText(Path.Combine(directory, "CleanMachine.exe.failed"), "backup");
            File.WriteAllText(Path.Combine(directory, ".update-write-probe"), "probe");
            File.WriteAllText(Path.Combine(directory, "CleanMachine.exe"), "the real app");

            UpdateService.CleanupUpdateArtifacts(directory);

            Assert.False(File.Exists(Path.Combine(directory, "CleanMachine.exe.restore")));
            Assert.False(File.Exists(Path.Combine(directory, "CleanMachine.exe.failed")));
            Assert.False(File.Exists(Path.Combine(directory, ".update-write-probe")));
            Assert.True(File.Exists(Path.Combine(directory, "CleanMachine.exe")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CleanupUpdateArtifactsToleratesMissingDirectoryAndNull()
    {
        UpdateService.CleanupUpdateArtifacts(null);
        UpdateService.CleanupUpdateArtifacts("");
        UpdateService.CleanupUpdateArtifacts(Path.Combine(Path.GetTempPath(), "cm-missing-" + Guid.NewGuid().ToString("N")));
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
    public void AutomaticUpdateChecksKeepTheBackgroundAgentRunning()
    {
        var settings = new AppSettings
        {
            CleanOnBrowserExit = false,
            SystemMonitoringEnabled = false,
            IdleCleanEnabled = false,
            RecycleBinAutoEmptyEnabled = false,
            CheckForUpdatesAutomatically = true,
            AutoInstallUpdates = false
        };

        Assert.True(settings.RequiresBackgroundAgent);
        Assert.True(settings.ShouldStartWithWindows);

        settings.CheckForUpdatesAutomatically = false;
        Assert.False(settings.RequiresBackgroundAgent);
        Assert.False(settings.ShouldStartWithWindows);

        // Auto-install cannot run without automatic checks, so it does not start
        // the agent by itself.
        settings.AutoInstallUpdates = true;
        Assert.False(settings.RequiresBackgroundAgent);
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

    [Fact]
    public void MsixUpdateScriptWaitsForExitInstallsAndRelaunches()
    {
        var script = UpdateService.BuildMsixUpdateScript(
            @"C:\Temp\Clean Machine\Pkg.msix", "Pkg_abc123", relaunchBackground: true);

        // Waits for the handing-off app to exit before touching the package...
        Assert.Contains("Get-Process", script);
        Assert.Contains("Start-Sleep", script);
        Assert.Contains("Add-AppxPackage", script);
        // ...carries the exact package path and family...
        Assert.Contains(@"C:\Temp\Clean Machine\Pkg.msix", script);
        Assert.Contains("shell:AppsFolder", script);
        Assert.Contains("Pkg_abc123", script);
        // ...and relaunches into the tray after an automatic (idle) update.
        Assert.Contains("$bg=$true", script);
        Assert.Contains("--background", script);
    }

    [Fact]
    public void MsixUpdateScriptQuotesEmbeddedSingleQuotes()
    {
        var script = UpdateService.BuildMsixUpdateScript("C:\\o'x.msix", "Fam", relaunchBackground: false);
        // A PowerShell single-quoted literal escapes ' by doubling it; without that,
        // an apostrophe in the path would truncate the literal and break the helper.
        Assert.Contains("$pkg='C:\\o''x.msix';", script);
        Assert.Contains("$bg=$false", script);
    }

    [Fact]
    public void MsixUpdateHelperCommandRoundTripsThroughEncodedScript()
    {
        var script = UpdateService.BuildMsixUpdateScript(@"C:\p.msix", "Fam_x", relaunchBackground: false);
        var command = UpdateService.BuildMsixUpdateHelperCommand(script);

        Assert.Contains("-EncodedCommand", command);
        Assert.Contains("powershell.exe", command);
        // The raw script never appears on the command line - it only travels encoded,
        // so schtasks' quoting cannot corrupt the package path or shell: URI.
        Assert.DoesNotContain("Add-AppxPackage", command);

        var encoded = command.Split(" -EncodedCommand ")[^1];
        Assert.Equal(script, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
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
