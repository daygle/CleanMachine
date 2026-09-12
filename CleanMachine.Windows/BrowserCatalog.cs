namespace CleanMachine.Windows;

public enum BrowserFamily { Chromium, Firefox, InternetExplorer }

/// <summary>Where an item's relative paths are anchored.</summary>
public enum BrowserItemRoot { Profile, UserData, Absolute }

/// <summary>One cleanable item within a browser profile (or user-data root).</summary>
public sealed record BrowserItemDefinition(string Id, string Name, bool Destructive, string Description);

/// <summary>A concrete path an item owns, relative to the profile or user-data root.</summary>
public sealed record BrowserItemPath(BrowserItemRoot Root, string Relative);

/// <summary>Everything needed to locate and clean one installed browser.</summary>
public sealed record BrowserDefinition(
    string Id,
    string Name,
    BrowserFamily Family,
    string[] ProcessNames,
    string[] ProfileRoots,
    string[] UserDataRoots,
    bool RootIsProfile);

/// <summary>Static catalog of the browsers we detect and the items each offers.
/// Safe items are recreatable caches; destructive items delete real user data and
/// are never selected by default in the UI.</summary>
public static class BrowserCatalog
{
    public static IReadOnlyList<BrowserDefinition> Browsers { get; } = Build();

    private static List<BrowserDefinition> Build()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        BrowserDefinition Chromium(string id, string name, string vendor, string[] processes)
        {
            var userData = Path.Combine(local, vendor, "User Data");
            return new(id, name, BrowserFamily.Chromium, processes,
                ProfileRoots: [userData, Path.Combine(pf, vendor, "User Data"), Path.Combine(pf86, vendor, "User Data")],
                UserDataRoots: [userData],
                RootIsProfile: false);
        }

        return
        [
            Chromium("chrome", "Google Chrome", Path.Combine("Google", "Chrome"), ["chrome"]),
            Chromium("edge", "Microsoft Edge", Path.Combine("Microsoft", "Edge"), ["msedge"]),
            Chromium("brave", "Brave", Path.Combine("BraveSoftware", "Brave-Browser"), ["brave"]),
            Chromium("vivaldi", "Vivaldi", "Vivaldi", ["vivaldi"]),
            new("opera", "Opera", BrowserFamily.Chromium, ["opera"],
                ProfileRoots: [Path.Combine(roaming, "Opera Software", "Opera Stable")],
                UserDataRoots: [Path.Combine(roaming, "Opera Software", "Opera Stable")],
                RootIsProfile: true),
            new("firefox", "Mozilla Firefox", BrowserFamily.Firefox, ["firefox"],
                ProfileRoots:
                [
                    Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"),
                    Path.Combine(local, "Mozilla", "Firefox", "Profiles")
                ],
                UserDataRoots: [],
                RootIsProfile: false),
            new("ie", "Internet Explorer", BrowserFamily.InternetExplorer, ["iexplore"],
                ProfileRoots: [], UserDataRoots: [], RootIsProfile: false)
        ];
    }

    public static BrowserDefinition? Find(string id)
        => Browsers.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the browser appears to be installed on this PC.</summary>
    public static bool IsInstalled(BrowserDefinition browser)
    {
        if (browser.Family == BrowserFamily.InternetExplorer)
        {
            var iexplore = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Internet Explorer", "iexplore.exe");
            return File.Exists(iexplore) || Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "INetCache"));
        }
        return Profiles(browser).Count > 0;
    }

    /// <summary>Profile directories for a browser (Chromium "Default"/"Profile N",
    /// Firefox hex profiles, or the Opera root itself).</summary>
    public static IReadOnlyList<string> Profiles(BrowserDefinition browser)
    {
        var profiles = new List<string>();
        if (browser.Family == BrowserFamily.InternetExplorer) return profiles;

        foreach (var root in browser.ProfileRoots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                if (browser.RootIsProfile)
                {
                    profiles.Add(root);
                    continue;
                }
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    // Chromium profiles contain Preferences; Firefox profiles are plain dirs.
                    if (browser.Family == BrowserFamily.Chromium
                        && !File.Exists(Path.Combine(dir, "Preferences"))) continue;
                    profiles.Add(dir);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return profiles;
    }

    public static IReadOnlyList<BrowserItemDefinition> ItemsFor(BrowserFamily family) => family switch
    {
        BrowserFamily.Firefox => FirefoxItems,
        BrowserFamily.InternetExplorer => InternetExplorerItems,
        _ => ChromiumItems
    };

    public static IReadOnlyList<BrowserItemPath> PathsFor(BrowserFamily family, string itemId) => (family, itemId) switch
    {
        // Chromium
        (BrowserFamily.Chromium, "cache") => Rel(BrowserItemRoot.Profile,
            ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "Media Cache", "DawnCache", "GrShaderCache", "ShaderCache"]),
        (BrowserFamily.Chromium, "sessions") => Rel(BrowserItemRoot.Profile,
            ["Current Session", "Current Tabs", "Last Session", "Last Tabs", "Sessions"]),
        (BrowserFamily.Chromium, "crash-reports") => Rel(BrowserItemRoot.UserData, ["Crashpad"]),
        (BrowserFamily.Chromium, "metrics") => Rel(BrowserItemRoot.UserData, ["BrowserMetrics"]),
        (BrowserFamily.Chromium, "bookmarks-backup") => Rel(BrowserItemRoot.Profile, ["Bookmarks.bak"]),
        (BrowserFamily.Chromium, "history") => Rel(BrowserItemRoot.Profile, ["History", "History-journal"]),
        (BrowserFamily.Chromium, "download-history") => Rel(BrowserItemRoot.Profile, ["History", "History-journal"]),
        (BrowserFamily.Chromium, "cookies") => Rel(BrowserItemRoot.Profile,
            [@"Network\Cookies", "Cookies", @"Network\Cookies-journal", @"Network\Cookies-wal", @"Network\Cookies-shm"]),
        (BrowserFamily.Chromium, "autofill") => Rel(BrowserItemRoot.Profile, ["Web Data", "Web Data-journal"]),
        (BrowserFamily.Chromium, "passwords") => Rel(BrowserItemRoot.Profile,
            ["Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal"]),

        // Firefox
        (BrowserFamily.Firefox, "cache") => Rel(BrowserItemRoot.Profile, ["cache2"]),
        (BrowserFamily.Firefox, "sessions") => Rel(BrowserItemRoot.Profile, ["sessionstore-backups", "sessionstore.jsonlz4"]),
        (BrowserFamily.Firefox, "crash-reports") => Rel(BrowserItemRoot.Profile, ["crashes", "minidumps"]),
        (BrowserFamily.Firefox, "bookmarks-backup") => Rel(BrowserItemRoot.Profile, ["bookmarkbackups"]),
        (BrowserFamily.Firefox, "history") => Rel(BrowserItemRoot.Profile, ["places.sqlite", "places.sqlite-wal", "places.sqlite-shm"]),
        (BrowserFamily.Firefox, "download-history") => Rel(BrowserItemRoot.Profile, ["places.sqlite", "places.sqlite-wal", "places.sqlite-shm"]),
        (BrowserFamily.Firefox, "cookies") => Rel(BrowserItemRoot.Profile, ["cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm"]),
        (BrowserFamily.Firefox, "autofill") => Rel(BrowserItemRoot.Profile, ["formhistory.sqlite"]),
        (BrowserFamily.Firefox, "passwords") => Rel(BrowserItemRoot.Profile, ["logins.json", "key4.db", "key3.db"]),
        (BrowserFamily.Firefox, "site-prefs") => Rel(BrowserItemRoot.Profile, ["permissions.sqlite", "content-prefs.sqlite"]),

        // Internet Explorer (folder-based items only; history/passwords need WinINet APIs)
        (BrowserFamily.InternetExplorer, "cache") => Abs(
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache")]),
        (BrowserFamily.InternetExplorer, "cookies") => Abs(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Cookies"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCookies")
        ]),

        _ => []
    };

    /// <summary>Item ids that need a preference edit rather than file deletion.</summary>
    public static bool IsPreferenceEdit(string itemId)
        => itemId is "last-download-location";

    private static BrowserItemPath[] Rel(BrowserItemRoot root, string[] paths)
        => paths.Select(p => new BrowserItemPath(root, p)).ToArray();

    private static BrowserItemPath[] Abs(string[] absolutePaths)
        => absolutePaths.Select(p => new BrowserItemPath(BrowserItemRoot.Absolute, p)).ToArray();

    private static readonly BrowserItemDefinition[] ChromiumItems =
    [
        new("cache", "Internet Cache", false, "Recreatable page, code, GPU, and service-worker caches"),
        new("sessions", "Sessions", false, "Session/tab restore files"),
        new("crash-reports", "Crash Reports", false, "Browser crash dumps"),
        new("metrics", "Metrics Temp Files", false, "Anonymous usage metrics pending upload"),
        new("bookmarks-backup", "Bookmarks Backup", false, "Automatic backup copy of bookmarks"),
        new("history", "Internet History", true, "Deletes the entire browsing history database"),
        new("download-history", "Download History", true, "Deletes recorded downloads (shares the history database)"),
        new("cookies", "Cookies", true, "Signs you out of every site until you log in again"),
        new("autofill", "AutoFill Form History", true, "Deletes saved form/address autofill entries"),
        new("passwords", "Saved Passwords", true, "Permanently deletes all passwords saved in the browser"),
        new("last-download-location", "Last Download Location", true, "Resets the default save folder to Downloads")
    ];

    private static readonly BrowserItemDefinition[] FirefoxItems =
    [
        new("cache", "Internet Cache", false, "Recreatable page and media caches"),
        new("sessions", "Sessions", false, "Session/tab restore backups"),
        new("crash-reports", "Crash Reports", false, "Browser crash dumps"),
        new("bookmarks-backup", "Bookmarks Backup", false, "Automatic backup copies of bookmarks"),
        new("history", "Internet History", true, "Deletes the entire browsing history database"),
        new("download-history", "Download History", true, "Deletes recorded downloads (shares the places database)"),
        new("cookies", "Cookies", true, "Signs you out of every site until you log in again"),
        new("autofill", "AutoFill Form History", true, "Deletes saved form autofill entries"),
        new("passwords", "Saved Passwords", true, "Permanently deletes all passwords saved in the browser"),
        new("site-prefs", "Site Preferences", true, "Per-site permissions and content preferences"),
        new("last-download-location", "Last Download Location", true, "Resets the default save folder to Downloads")
    ];

    private static readonly BrowserItemDefinition[] InternetExplorerItems =
    [
        new("cache", "Internet Cache (Temporary Internet Files)", false, "Recreatable web caches"),
        new("cookies", "Cookies", true, "Signs you out of every site until you log in again")
    ];
}
