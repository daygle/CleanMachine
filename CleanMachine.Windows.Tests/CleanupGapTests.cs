using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins the fixes for gaps between the cleanup areas: browser caches are
/// only reached through Browser Cleaner, every supported browser can be monitored
/// and scheduled, schedules choose their browsers and apps, and broken shortcuts
/// are judged conservatively. Read-only: nothing here deletes a file.</summary>
public sealed class CleanupGapTests
{
    // ---- Browser caches belong to Browser Cleaner only ----

    [Fact]
    public void ApplicationCleanupNeverTargetsABrowserProfile()
    {
        // Application Cleanup has no "browser is open" check and no recent-file
        // window, and it only knew the Default profile. Browser caches go through
        // Browser Cleaner, which has both and covers every profile.
        var browserFolders = new[]
        {
            @"Google\Chrome", @"Microsoft\Edge\", "BraveSoftware", @"Vivaldi\", "Opera Software", @"Mozilla\Firefox"
        };
        var paths = AppCatalog.Definitions
            .SelectMany(d => d.TempLocations.SelectMany(l => l.Entries).Select(e => (d.Id, e.RelativePath)));

        Assert.DoesNotContain(paths, p =>
            browserFolders.Any(folder => p.RelativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void EverySupportedBrowserIsMonitoredForExit()
    {
        var monitored = BackgroundAgent.ProcessToBrowser.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var browser in BrowserCatalog.Browsers.Where(b => b.Family != BrowserFamily.InternetExplorer))
            Assert.Contains(browser.Id, monitored);
        Assert.Equal("edge", BackgroundAgent.ProcessToBrowser["msedge"]);
        Assert.DoesNotContain("ie", monitored);
    }

    [Fact]
    public void ABrowserWithNoSavedMonitorIsNotCleanedOnExit()
    {
        // Brave, Vivaldi and Opera were never listed before; existing settings have
        // no monitor for them, and that must read as "off" rather than as a new
        // background clean the user never agreed to.
        var settings = new AppSettings();

        Assert.Null(settings.FindBrowserMonitor("brave"));
        Assert.Null(settings.FindBrowserMonitor("vivaldi"));
        Assert.Null(settings.FindBrowserMonitor("opera"));
    }

    // ---- Schedules choose their browsers and apps ----

    [Fact]
    public void LegacySchedulesKeepTheirAllBrowsersAndAllAppsMeaning()
    {
        // A schedule saved before the per-item lists existed has null lists; they
        // must stay null (not become "nothing") so the schedule keeps cleaning what
        // it always did.
        var settings = new AppSettings
        {
            Schedules = [new CleanupSchedule { Id = "legacy1", CleanBrowserCache = true, CleanAppTempFiles = true }]
        }.Sanitize();

        var schedule = Assert.Single(settings.Schedules);
        Assert.Null(schedule.BrowserIds);
        Assert.Null(schedule.AppIds);
        Assert.True(ScheduledTask.HasWork(schedule));
    }

    [Fact]
    public void ChosenScheduleBrowsersAndAppsRoundTrip()
    {
        var settings = new AppSettings
        {
            Schedules =
            [
                new CleanupSchedule
                {
                    Id = "chosen1",
                    CleanBrowserCache = true,
                    BrowserIds = ["brave", "vivaldi"],
                    CleanAppTempFiles = true,
                    AppIds = ["7zip"]
                }
            ]
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var schedule = Assert.Single(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.Sanitize().Schedules);

        Assert.Equal(new[] { "brave", "vivaldi" }, schedule.BrowserIds);
        Assert.Equal(new[] { "7zip" }, schedule.AppIds);
    }

    [Fact]
    public void ScheduleBrowserSelectionOnlyEverClearsTheCache()
    {
        var selection = QuickCleanService.BrowserCacheSelection(BrowserCatalog.Browsers.Select(b => b.Id));

        Assert.All(selection, s => Assert.Equal("cache", s.ItemId));
        Assert.DoesNotContain(selection, s => s.BrowserId == "ie");
    }

    // ---- Broken shortcuts ----

    [Fact]
    public void BrokenShortcutsAreReviewTierAndNeverUnattended()
    {
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == "explorer-broken-shortcuts");

        Assert.Equal(CleanupKind.BrokenShortcuts, category.Kind);
        Assert.Equal(CleanupRisk.Review, category.Risk);
        Assert.False(category.EnabledByDefault);
        Assert.False(category.Kind.IsAction());
        Assert.False(QuickCleanService.IsWindowsSelected(category, new AppSettings()));
    }

    [Theory]
    [InlineData(@"C:\Gone\app.exe", false, true, true)]     // missing on a local fixed drive
    [InlineData(@"C:\Here\app.exe", true, true, false)]     // target exists
    [InlineData(@"E:\Gone\app.exe", false, false, false)]   // removable/network/missing drive: unknown
    [InlineData(@"\\server\share\app.exe", false, true, false)] // network share: may just be offline
    [InlineData(@"relative\app.exe", false, true, false)]   // not a full path
    [InlineData("", false, true, false)]                     // URL, Store app or shell item
    [InlineData(null, false, true, false)]
    public void OnlyAProvablyMissingLocalTargetIsBroken(string? target, bool exists, bool fixedDrive, bool expected)
        => Assert.Equal(expected, BrokenShortcuts.IsBrokenTarget(target, _ => exists, _ => fixedDrive));

    [Fact]
    public void BrokenShortcutDeletionIsConfinedToShortcutFolders()
    {
        Assert.False(BrokenShortcuts.IsInLocations(@"C:\Windows\System32\notepad.exe"));
        foreach (var (root, _) in BrokenShortcuts.Locations())
        {
            Assert.True(BrokenShortcuts.IsInLocations(Path.Combine(root, "Gone.lnk")));
            Assert.False(BrokenShortcuts.IsInLocations(Path.Combine(root, "Notes.txt")));
        }
    }
}
