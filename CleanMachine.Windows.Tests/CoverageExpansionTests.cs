using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins the browsers, Windows caches, developer tool caches and apps added
/// in one batch: each reaches only recreatable caches and logs, nothing needs
/// administrator rights, and browsers sharing an executable name are handled.
/// Read-only: nothing here touches a real file or process.</summary>
public sealed class CoverageExpansionTests
{
    // ---- Browsers ----

    [Theory]
    [InlineData("opera-gx", BrowserFamily.Chromium)]
    [InlineData("chromium", BrowserFamily.Chromium)]
    [InlineData("thorium", BrowserFamily.Chromium)]
    [InlineData("librewolf", BrowserFamily.Firefox)]
    [InlineData("waterfox", BrowserFamily.Firefox)]
    [InlineData("floorp", BrowserFamily.Firefox)]
    public void NewBrowsersUseTheirFamilysLayout(string id, BrowserFamily family)
    {
        var browser = BrowserCatalog.Find(id);

        Assert.NotNull(browser);
        Assert.Equal(family, browser!.Family);
        Assert.NotEmpty(browser.ProfileRoots);
        Assert.NotEmpty(browser.ProcessNames);
    }

    [Theory]
    [InlineData("opera")]
    [InlineData("opera-gx")]
    public void OperaCachesAreFoundInLocalAppData(string id)
    {
        // Opera keeps the profile in Roaming but the disk cache in Local AppData.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var browser = BrowserCatalog.Find(id)!;

        Assert.Contains(browser.ProfileRoots, r => NativeSafety.IsWithin(r, local));
        Assert.Contains(browser.ProfileRoots, r => NativeSafety.IsWithin(r, roaming));
    }

    [Fact]
    public void BrowsersSharingAnExecutableAllGetTheirExitClean()
    {
        // chrome.exe is Google Chrome and Chromium; opera.exe is Opera and Opera GX.
        Assert.Equal(new[] { "chrome", "chromium" }, BackgroundAgent.ProcessToBrowsers["chrome"].Order());
        Assert.Equal(new[] { "opera", "opera-gx" }, BackgroundAgent.ProcessToBrowsers["opera"].Order());
    }

    [Fact]
    public void ASharedExecutableIsNamedAfterTheBrowserBeingCleaned()
    {
        Assert.Equal("Chromium", BrowserCleanupService.DisplayNameForProcess("chrome", ["chromium"]));
        Assert.Equal("Opera GX", BrowserCleanupService.DisplayNameForProcess("opera", ["opera-gx"]));
        Assert.Equal("Google Chrome", BrowserCleanupService.DisplayNameForProcess("chrome"));
    }

    [Fact]
    public void NoBrowserNeedsAGenericProcessName()
    {
        // A generic executable name (Yandex's browser.exe) could match an unrelated
        // app when closing or watching browsers, so such browsers are not listed.
        Assert.DoesNotContain(BrowserCatalog.Browsers,
            b => b.ProcessNames.Contains("browser", StringComparer.OrdinalIgnoreCase));
    }

    // ---- Windows caches ----

    [Theory]
    [InlineData("system-shell-caches", CleanupRisk.Safe)]
    [InlineData("system-legacy-history", CleanupRisk.Safe)]
    [InlineData("system-burn-staging", CleanupRisk.Review)]
    public void NewWindowsCategoriesAreOffByDefaultAndPerUser(string id, CleanupRisk risk)
    {
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == id);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(risk, category.Risk);
        // New categories never start cleaning for existing users on their own.
        Assert.False(category.EnabledByDefault);
        Assert.Equal(CleanupKind.Files, category.Kind);
        Assert.True(NativeSafety.IsWithin(category.Path!, local));
    }

    [Fact]
    public void FilesWaitingToBeBurnedAreNeverCleanedUnattended()
    {
        // They may be the only copy of what the user meant to put on a disc.
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == "system-burn-staging");
        Assert.False(QuickCleanService.IsWindowsSelected(category, new AppSettings()));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Windows\History\desktop.ini", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\DESKTOP.INI", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\notes.ini", false)]
    public void DesktopIniIsNeverCleaned(string path, bool isProtected)
        => Assert.Equal(isProtected, WindowsCleanupService.IsProtectedFile(path));

    // ---- Developer tools and apps ----

    [Theory]
    [InlineData("python-poetry", "virtualenvs")]
    [InlineData("google-drive", "content_cache")]
    [InlineData("minecraft-launcher", "saves")]
    [InlineData("minecraft-launcher", "screenshots")]
    [InlineData("composer", "vendor")]
    public void NewEntriesNeverOfferEnvironmentsUserFilesOrUnsyncedData(string appId, string forbidden)
    {
        var paths = AppCatalog.Definitions.Single(d => d.Id == appId)
            .TempLocations.SelectMany(l => l.Entries).Select(e => e.RelativePath);

        Assert.DoesNotContain(paths, p =>
            p.Split('\\').Any(segment => segment.Equals(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("canva")]
    [InlineData("element")]
    [InlineData("mattermost")]
    [InlineData("insomnia")]
    [InlineData("docker-desktop")]
    [InlineData("unity-hub")]
    public void ElectronAppsOnlyReachTheirDiskCaches(string appId)
    {
        // An Electron profile also holds cookies, local storage and settings; only
        // the three disk-cache folders are recreatable.
        var leaves = AppCatalog.Definitions.Single(d => d.Id == appId)
            .TempLocations.SelectMany(l => l.Entries)
            .Select(e => e.RelativePath.Split('\\').Last())
            .ToList();

        Assert.NotEmpty(leaves);
        Assert.All(leaves, leaf => Assert.Contains(leaf, new[] { "Cache", "Code Cache", "GPUCache" }));
    }

    [Fact]
    public void NewEntriesNeverReachIntoProgramFolders()
    {
        // Program Files and ProgramData normally need administrator rights to
        // delete from, which the Store build never has.
        var added = new[]
        {
            "python-poetry", "python-uv", "composer", "bun", "visual-studio", "canva", "element",
            "mattermost", "insomnia", "docker-desktop", "unity-hub", "riot-client", "google-drive", "minecraft-launcher"
        };

        foreach (var id in added)
        {
            var app = AppCatalog.Find(id);
            Assert.True(app is not null, $"'{id}' is missing from the catalog.");
            Assert.All(app!.TempLocations, l =>
                Assert.Contains(l.Root, new[] { AppDataRoot.LocalAppData, AppDataRoot.RoamingAppData, AppDataRoot.UserProfile }));
        }
    }
}
