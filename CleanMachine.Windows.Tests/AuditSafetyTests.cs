using System.Buffers.Binary;
using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

// Scratch files only. The collection also protects the global cleaning counter.
[Collection("Cleaning")]
public sealed class AuditSafetyTests
{
    private const string RunRoot = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void ParentLinkCannotRedirectEnumerationOrDeletion()
    {
        using var scratch = new Scratch();
        var outside = Directory.CreateDirectory(Path.Combine(scratch.Root, "outside")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(outside, "Cache")).FullName;
        var file = Path.Combine(nested, "keep.txt");
        File.WriteAllText(file, "valuable data");
        var link = Path.Combine(scratch.Root, "redirected-profile");
        Directory.CreateSymbolicLink(link, outside);
        var target = Path.Combine(link, "Cache");
        Assert.True(NativeSafety.IsReparsePoint(Path.Combine(target, "keep.txt")));
        Assert.Empty(FileEnumeration.Files(target));
        var result = BrowserCleanupService.DeletePath(target, null, null, default);
        Assert.Equal(0, result.Removed);
        Assert.Equal("valuable data", File.ReadAllText(file));
        Directory.Delete(link);
    }

    [Fact]
    public void OrdinaryCacheFilesAreStillCleanedAndExcludedEmptyFoldersSurvive()
    {
        using var scratch = new Scratch();
        var cache = Directory.CreateDirectory(Path.Combine(scratch.Root, "cache")).FullName;
        var excluded = Directory.CreateDirectory(Path.Combine(cache, "keep-empty")).FullName;
        File.WriteAllText(Path.Combine(cache, "junk"), "1234");
        var result = BrowserCleanupService.DeletePath(cache, new HashSet<string> { excluded }, null, default);
        Assert.Equal(1, result.Removed);
        Assert.Equal(4, result.Bytes);
        Assert.True(Directory.Exists(excluded));
    }

    [Fact]
    public void CancelledBrowserDeletionLeavesFilesIntact()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "keep");
        File.WriteAllText(file, "1234");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            BrowserCleanupService.DeletePath(scratch.Root, null, null, cts.Token));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task BrowserSelectionIsEnumeratedOnce()
    {
        var enumerations = 0;
        IEnumerable<(string BrowserId, string ItemId)> Selection()
        {
            enumerations++;
            if (enumerations > 1) throw new InvalidOperationException("Selection cannot be replayed");
            yield return ("not-a-browser", "cache");
        }
        var report = await new BrowserCleanupService().CleanItemsAsync(Selection());
        Assert.Equal(1, enumerations);
        Assert.Equal(0, report.Result.ItemsRemoved);
    }

    [Fact]
    public void PreferenceEditsRespectExclusionsAndPreserveOtherKeys()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "Preferences");
        const string original = "{\"download\":{\"default_directory\":\"D:/files\",\"prompt_for_download\":true},\"other\":42}";
        File.WriteAllText(path, original);
        var browser = new BrowserDefinition("test", "Test", BrowserFamily.Chromium, [], [], [], false);
        var issues = new List<CleanupIssue>();
        Assert.False(BrowserCleanupService.ApplyPreferenceEdit(browser, [scratch.Root], issues, new HashSet<string> { path }));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(BrowserCleanupService.ApplyPreferenceEdit(browser, [scratch.Root], issues, null));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(json["download"]!["default_directory"]);
        Assert.True(json["download"]!["prompt_for_download"]!.GetValue<bool>());
        Assert.Equal(42, json["other"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllText(path + ".cleanmachine.bak"));
        Assert.Empty(Directory.GetFiles(scratch.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("user_pref(\"browser.download.dir\", \"C:/downloads\");", true)]
    [InlineData("  user_pref(\"browser.download.lastDir\", \"C:/downloads\");", true)]
    [InlineData("user_pref(\"browser.download.directory_upgrade\", true);", false)]
    [InlineData("user_pref(\"other\", \"browser.download.dir\");", false)]
    [InlineData("// browser.download.dir", false)]
    public void FirefoxPreferenceFilterMatchesOnlyTheIntendedKeys(string line, bool remove)
        => Assert.Equal(remove, BrowserCleanupService.IsDownloadDirectoryPreference(line));

    [Fact]
    public void AutomaticBrowserDefaultsPreserveRecoveryAndBookmarkData()
    {
        foreach (var family in new[] { BrowserFamily.Chromium, BrowserFamily.Firefox })
            Assert.All(BrowserCatalog.ItemsFor(family).Where(i => i.Id is "sessions" or "bookmarks-backup"),
                i => Assert.True(i.Destructive));
        var settings = new AppSettings();
        Assert.DoesNotContain("sessions", settings.EffectiveExitItems("chrome"));
        Assert.DoesNotContain("bookmarks-backup", settings.EffectiveExitItems("firefox"));
        Assert.Empty(BrowserCatalog.PathsFor(BrowserFamily.Firefox, "history"));
        Assert.Empty(BrowserCatalog.PathsFor(BrowserFamily.Firefox, "download-history"));
    }

    [Theory]
    [InlineData("microsoft-office", "OfficeFileCache")]
    [InlineData("mediaplayer", "CurrentDatabase_*.wmdb")]
    public void ApplicationTempCatalogDoesNotDeletePendingUploadsOrMediaLibrary(string app, string leaf)
        => Assert.DoesNotContain(AppCatalog.Find(app)!.TempLocations.SelectMany(l => l.Entries),
            e => e.RelativePath.Split('\\', '/').Last().Equals(leaf, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task BackupVerificationRequiresProvenanceAndFullCoverage()
    {
        using var scratch = new Scratch();
        var file = Path.Combine(scratch.Root, "backup.reg");
        await File.WriteAllTextAsync(file, "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\" + RunRoot + "]\r\n\"App\"=\"old\"\r\n");
        var finding = new RegistryFinding("HKCU", RunRoot, "missing program", true, 75, "Windows Startup", "App");
        var review = new RegistryReview([finding], [new RegistryBackup(file, DateTimeOffset.UtcNow)]);
        Assert.False(await RegistryCareService.VerifyReviewBackupsAsync(review, []));
        var manifest = Path.Combine(scratch.Root, "provenance.json");
        BackupProvenance.Record(manifest, file, RunRoot, DateTimeOffset.UtcNow);
        var entries = BackupProvenance.Load(manifest);
        Assert.True(await RegistryCareService.VerifyReviewBackupsAsync(review, entries));
        Assert.False(await RegistryCareService.VerifyReviewBackupsAsync(review with
        {
            Backups = [review.Backups[0], new RegistryBackup(Path.Combine(scratch.Root, "missing.reg"), DateTimeOffset.UtcNow)]
        }, entries));
        Assert.False(await RegistryCareService.VerifyReviewBackupsAsync(review with
        {
            Findings = [finding, finding with { Path = RunRoot + "Once" }]
        }, entries));
        await File.AppendAllTextAsync(file, "tampered");
        Assert.False(await RegistryCareService.VerifyReviewBackupsAsync(review, entries));
    }

    [Fact]
    public void BackupNamesDoNotCollideOnSameLeafOrTimestampAndStayBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var names = Enumerable.Range(0, 1000).Select(i => RegistryCareService.BackupFileName(now)).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, name => Assert.InRange(name.Length, 1, 100));
    }

    [Fact]
    public void ExportProbeNeverTruncatesAnExistingRestorePoint()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "existing.reg");
        File.WriteAllText(path, "keep backup");
        Assert.Throws<InvalidOperationException>(() => RegistryCareService.EnsureExportTargetUsable(path));
        Assert.Equal("keep backup", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("REGEDIT4 injected")]
    [InlineData("Windows Registry Editor Version 5.00 injected")]
    [InlineData("garbage")]
    public async Task BackupValidationRejectsHeaderPrefixesAndNonRegFiles(string header)
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "bad.reg");
        await File.WriteAllTextAsync(path, header);
        Assert.False(await RegistryCareService.ValidateBackupAsync(new(path, DateTimeOffset.UtcNow)));
        var text = Path.Combine(scratch.Root, "note.txt");
        await File.WriteAllTextAsync(text, "REGEDIT4");
        Assert.False(await RegistryCareService.ValidateBackupAsync(new(text, DateTimeOffset.UtcNow)));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    public void RecycleBinVersionsUseTheSameDeletionTimestampOffset(long version)
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "$Itest");
        var time = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc);
        var data = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(data, version);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 1024);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), time.ToFileTimeUtc());
        File.WriteAllBytes(path, data);
        Assert.Equal(time, RecycleBinService.ReadDeletionTimeUtc(path));
        BinaryPrimitives.WriteInt64LittleEndian(data, 999);
        File.WriteAllBytes(path, data);
        Assert.Null(RecycleBinService.ReadDeletionTimeUtc(path));
    }

    [Fact]
    public void FailedCancelledAndEmptySchedulesNeverApplyPowerActions()
    {
        var schedule = new CleanupSchedule { WindowsCategoryIds = ["system-temp"], AfterClean = ScheduleAction.Shutdown };
        Assert.True(ScheduleService.MayApplyPowerAction(schedule, [], default));
        Assert.False(ScheduleService.MayApplyPowerAction(schedule, ["Cleanup failed"], default));
        Assert.False(ScheduleService.MayApplyPowerAction(new CleanupSchedule(), [], default));
        Assert.False(ScheduleService.MayApplyPowerAction(new CleanupSchedule
        {
            WindowsCategoryIds = ["stale-category"], AfterClean = ScheduleAction.Shutdown
        }, [], default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(ScheduleService.MayApplyPowerAction(schedule, [], cts.Token));
    }

    [Fact]
    public async Task HelperOutputIsBoundedButFullyDrained()
    {
        using var reader = new StringReader(new string('x', ScheduleService.ProcessOutcome.MaxOutputLength * 4));
        var text = await ScheduleService.ReadBoundedOutputAsync(reader);
        Assert.Equal(ScheduleService.ProcessOutcome.MaxOutputLength, text.Length);
        Assert.Equal(-1, reader.Read());
    }

    [Fact]
    public void CorruptExplicitSelectionsNeverExpandToDefaultAll()
    {
        var settings = new AppSettings
        {
            QuickCleanApps = Enumerable.Range(0, 4097).Select(i => "app" + i).ToHashSet(),
            EnabledCleanupCategories = ["SYSTEM-TEMP"],
            Schedules = [new() { Id = "duplicate" }, new() { Id = "DUPLICATE" }]
        }.Sanitize();
        Assert.NotNull(settings.QuickCleanApps);
        Assert.Empty(settings.QuickCleanApps);
        Assert.Contains("system-temp", settings.EnabledCleanupCategories);
        Assert.Single(settings.Schedules);
        Assert.Equal(long.MaxValue, CleanupStatsStore.AddNonNegative(long.MaxValue - 1, 42));
        Assert.Equal(0, CleanupStatsStore.AddNonNegative(-42, -1));
    }

    [Fact]
    public void ProvenanceIgnoresStructurallyInvalidRecords()
    {
        using var scratch = new Scratch();
        var manifest = Path.Combine(scratch.Root, "provenance.json");
        File.WriteAllText(manifest, "[null,{}, {\"FileName\":\"../payload.reg\",\"KeyRoot\":\"x\"}]");
        Assert.Empty(BackupProvenance.Load(manifest));
    }

    [Fact]
    public void StreamingAppScanReportsTheActualFileCountAndBytes()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(Path.Combine(scratch.Root, "nested"));
        File.WriteAllText(Path.Combine(scratch.Root, "one"), "1234");
        File.WriteAllText(Path.Combine(scratch.Root, "nested", "two"), "abc");
        var result = AppCleanupService.SizeDirectory(scratch.Root, "Test");
        Assert.Equal(2, result.FileCount);
        Assert.Equal(7, result.Bytes);
    }

    [Fact]
    public void SharedStartupKeysRequireANamedValueFinding()
    {
        var finding = new RegistryFinding("HKCU", RunRoot, "missing", true, 75, "Windows Startup");
        Assert.False(RegistryCareService.IsCleanable(finding));
        Assert.True(RegistryCareService.IsCleanable(finding with { ValueName = "OldApp" }));
    }

    [Fact]
    public async Task BackupValidationAcceptsExactAsciiAndUtf16Headers()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "backup.reg");
        await File.WriteAllTextAsync(path, "REGEDIT4\r\n");
        Assert.True(await RegistryCareService.ValidateBackupAsync(new(path, DateTimeOffset.UtcNow)));
        await File.WriteAllTextAsync(path, "Windows Registry Editor Version 5.00\r\n", System.Text.Encoding.Unicode);
        Assert.True(await RegistryCareService.ValidateBackupAsync(new(path, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void NativeLifecycleFixesKeepTheirSafetyGuards()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CleanMachine.Windows", "App.xaml.cs")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string Source(string name) => File.ReadAllText(Path.Combine(directory!.FullName, "CleanMachine.Windows", name));
        var app = Source("App.xaml.cs");
        Assert.Contains("var agent = _agent;", app);
        Assert.Contains("var cts = _agentCts;", app);
        Assert.Contains("await agent.RunAsync(cts.Token)", app);
        Assert.Contains("ReferenceEquals(_agentCts, cts)", app);
        Assert.Contains("return _trayIcon.IsVisible;", Source("MainWindow.xaml.cs"));
        Assert.Contains("if (removeData) Try(() =>", Source("MsixUninstallService.cs"));
        Assert.Contains("Directory.EnumerateDirectories(bin, sid)", Source("RecycleBinService.cs"));
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cm-audit-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
