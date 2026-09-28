using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Regression coverage for fixes from the codebase audit: how unattended
/// browser cleans choose what to clean, how Application Cleanup identifies items
/// and detects Store apps, and a few input-hardening rules. Pure logic only -
/// nothing here deletes a file or touches the registry.</summary>
public sealed class AuditRegressionTests
{
    [Fact]
    public void BrowserCacheSelectionOnlyEverChoosesTheNonDestructiveCacheItem()
    {
        // Quick Clean and schedules run unattended; the only item they may pick is
        // the cache, whatever browser ids they are handed.
        var selection = QuickCleanService.BrowserCacheSelection(
            BrowserCatalog.Browsers.Select(b => b.Id).Append("not-a-browser"));

        Assert.All(selection, pair => Assert.Equal("cache", pair.ItemId));
        foreach (var family in new[] { BrowserFamily.Chromium, BrowserFamily.Firefox })
        {
            var cache = BrowserCatalog.ItemsFor(family).Single(i => i.Id == "cache");
            Assert.False(cache.Destructive);
        }
    }

    [Fact]
    public void BrowserCacheSelectionSkipsInternetExplorerAndDuplicates()
    {
        // IE's cache is the Windows Internet Cache, which Windows Cleanup owns.
        var selection = QuickCleanService.BrowserCacheSelection(["ie", "IE", "chrome", "chrome"]);

        Assert.DoesNotContain(selection, pair => pair.BrowserId == "ie");
        Assert.True(selection.Count(pair => pair.BrowserId == "chrome") <= 1);
    }

    [Fact]
    public void AllItemsIdentifiesItemsByPathAndIgnoresAppsThatAreNotInstalled()
    {
        var scans = new[]
        {
            new AppScan("a", "A", "G", false, true,
            [
                new AppTempItem("one", 1, 1, @"C:\x\one"),
                new AppTempItem("two", 2, 1, @"C:\x\two")
            ]),
            new AppScan("b", "B", "G", false, false, [new AppTempItem("gone", 1, 1, @"C:\y")])
        };

        var items = AppCleanupService.AllItems(scans);

        Assert.Equal(new (string AppId, string ItemPath)[] { ("a", @"C:\x\one"), ("a", @"C:\x\two") }, items);
    }

    [Fact]
    public void StorePackageRootStopsAtTheAcOrTempStateSegment()
    {
        var root = Path.Combine("C:", "Users", "ISAAC", "AppData", "Local");

        Assert.Equal(Path.Combine(root, "Packages", "Microsoft.Todo_8wekyb3d8bbwe"),
            AppCleanupService.StorePackageRoot(root, @"Packages\Microsoft.Todo_8wekyb3d8bbwe\AC\Temp"));
        Assert.Equal(Path.Combine(root, "Packages", "Microsoft.Todo_8wekyb3d8bbwe"),
            AppCleanupService.StorePackageRoot(root, @"Packages\Microsoft.Todo_8wekyb3d8bbwe\TempState"));
    }

    [Fact]
    public void StorePackageRootIgnoresAcInsideOtherSegmentsAndRejectsEntriesWithoutOne()
    {
        // A profile or package name that merely contains "AC" must not end the
        // package path early (that made every Store app look installed).
        var root = Path.Combine("C:", "Users", "ISAAC", "AppData", "Local");

        Assert.Equal(Path.Combine(root, "Packages", "MACHINE.App_abc"),
            AppCleanupService.StorePackageRoot(root, @"Packages\MACHINE.App_abc\AC\INetCache"));
        Assert.Null(AppCleanupService.StorePackageRoot(root, @"Packages\NoMarker\LocalState"));
        Assert.Null(AppCleanupService.StorePackageRoot(root, @"AC\Temp"));
    }

    [Theory]
    [InlineData("abc\u00e9")]      // non-ASCII letter
    [InlineData("12\u0663")]       // Arabic-Indic digit
    [InlineData("\uff21\uff22")]   // full-width letters
    public void ScheduleIdsMustBeAscii(string id)
        => Assert.False(ScheduledTask.IsValidScheduleId(id));

    [Fact]
    public void SanitizeRepairsAnOutOfRangeDayOfWeek()
    {
        var settings = new AppSettings
        {
            Schedules = [new CleanupSchedule { DayOfWeek = (DayOfWeek)42 }]
        };

        settings.Sanitize();

        Assert.Equal(DayOfWeek.Sunday, settings.Schedules.Single().DayOfWeek);
    }

    [Fact]
    public void CategoryResultSkippedDefaultsToZero()
        => Assert.Equal(0, new CleanupCategoryResult("Temporary Files", 2, 2048).Skipped);

    [Fact]
    public void StartupResolversAgreeWithTheRegistryCareResolver()
    {
        // The Startup Apps page and Registry Care must never disagree on whether an
        // entry points at a missing program.
        foreach (var command in new[]
                 {
                     @"""C:\Program Files\App\app.exe"" --flag",
                     @"C:\App\app.exe --flag",
                     @"""%ProgramFiles%\App\app.exe""",
                     "cmd /c echo hi",
                     @"""C:\App\app.exe"
                 })
        {
            Assert.Equal(CleanupService.ResolveStartupExecutable(command),
                StartupAppsService.ResolveExecutable(command));
        }
    }
}
