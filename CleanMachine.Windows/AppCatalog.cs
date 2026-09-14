namespace CleanMachine.Windows;

/// <summary>Where an app stores its data relative to a root.</summary>
public enum AppDataRoot
{
    LocalAppData,
    RoamingAppData,
    ProgramFiles,
    ProgramFilesX86,
    UserProfile,
    WindowsTemp
}

/// <summary>A known temp/cache file location for an application.</summary>
public sealed record AppTempEntry(string RelativePath, string Description);

/// <summary>A known application that can be cleaned.</summary>
public sealed record AppDefinition(
    string Id,
    string Name,
    string Group,
    bool IsStoreApp,
    IReadOnlyList<(AppDataRoot Root, IReadOnlyList<AppTempEntry> Entries)> TempLocations);

/// <summary>Detection result for one app.</summary>
public sealed record AppScan(
    string Id,
    string Name,
    string Group,
    bool IsStoreApp,
    bool Installed,
    IReadOnlyList<AppTempItem> Items);

/// <summary>A cleanable temp item for one app.</summary>
public sealed record AppTempItem(string Description, long Bytes, int FileCount, string FullPath);

/// <summary>Catalog of known applications and their temp file locations.</summary>
public static class AppCatalog
{
    public static IReadOnlyList<AppDefinition> Definitions { get; } =
    [
        // ---- Desktop Apps ----
        new("7zip", "7-Zip", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("7-Zip\\Temp", "Extraction temp files"),
                new("7-Zip\\History", "File history")
            ])
        ]),

        new("github-desktop", "GitHub Desktop", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("GitHub Desktop\\Cache", "App cache"),
                new("GitHub Desktop\\Code Cache", "Code cache"),
                new("GitHub Desktop\\GPUCache", "GPU cache"),
                new("GitHub Desktop\\logs", "Log files"),
                new("GitHub Desktop\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("notepadpp", "Notepad++", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Notepad++\\backup", "Backup files"),
                new("Notepad++\\cache", "Cache files"),
                new("Notepad++\\session.xml", "Old session data")
            ])
        ]),

        new("nodejs", "Node.js", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("npm-cache", "npm cache"),
                new("pnpm-store", "pnpm store"),
                new("yarn\\Cache", "yarn cache")
            ])
        ]),

        new("steam", "Steam", "Desktop App", false,
        [
            (AppDataRoot.ProgramFilesX86, [
                new("Steam\\appcache", "App cache"),
                new("Steam\\logs", "Log files"),
                new("Steam\\config\\htmlcache", "HTML cache"),
                new("Steam\\dumps", "Crash dumps")
            ]),
            (AppDataRoot.LocalAppData, [
                new("Steam\\htmlcache", "HTML cache")
            ])
        ]),

        new("vlc", "VLC Media Player", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("vlc\\cache", "Cache files"),
                new("vlc\\logs", "Log files")
            ])
        ]),

        new("zoom", "Zoom", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Zoom\\logs", "Log files"),
                new("Zoom\\data", "Cache data"),
                new("Zoom\\Updates", "Old update files")
            ])
        ]),

        new("microsoft-office", "Microsoft Office", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Office\\16.0\\OfficeFileCache", "Office file cache"),
                new("Microsoft\\Office\\16.0\\Wef\\Cache", "Workflow cache"),
                new("Microsoft\\Office\\16.0\\OfficeNotification", "Notification cache")
            ]),
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Office\\Recent", "Recent documents")
            ])
        ]),

        new("onedrive", "Microsoft OneDrive", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("OneDrive\\logs", "Log files"),
                new("OneDrive\\cache", "Cache files"),
                new("OneDrive\\Updates", "Old update files")
            ])
        ]),

        new("microsoft-edge", "Microsoft Edge", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Edge\\User Data\\Default\\Cache", "Browser cache"),
                new("Microsoft\\Edge\\User Data\\Default\\Code Cache", "Code cache"),
                new("Microsoft\\Edge\\User Data\\Default\\GPUCache", "GPU cache"),
                new("Microsoft\\Edge\\User Data\\Default\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("activity-history", "Activity History", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("ConnectedDevicesPlatform", "Activity history"),
                new("Microsoft\\Windows\\ActivityCache", "Activity cache")
            ])
        ]),

        new("defender", "Windows Defender", "Desktop App", false,
        [
            (AppDataRoot.WindowsTemp, [
                new("MpCmdRun.log", "Defender scan log"),
                new("MpScanResults-*.log", "Old scan results")
            ])
        ]),

        new("mediaplayer", "Windows Media Player", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Media Player\\CurrentDatabase_*.wmdb", "Old database"),
                new("Microsoft\\Media Player\\Cache", "Cache files")
            ])
        ]),

        new("autoplay", "Windows AutoPlay Devices", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Windows\\Autoplay", "AutoPlay cache")
            ])
        ]),

        new("search", "Microsoft Search", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Search\\Data\\Applications\\Windows\\Windows.edb", "Search index (will rebuild)"),
                new("Microsoft\\Search\\Data\\Temp", "Search temp files")
            ])
        ]),

        new("discord", "Discord", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("discord\\Cache", "App cache"),
                new("discord\\Code Cache", "Code cache"),
                new("discord\\GPUCache", "GPU cache"),
                new("discord\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("slack", "Slack", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Slack\\Cache", "App cache"),
                new("Slack\\Code Cache", "Code cache"),
                new("Slack\\GPUCache", "GPU cache"),
                new("Slack\\Service Worker\\CacheStorage", "Service worker cache"),
                new("Slack\\logs", "Log files")
            ])
        ]),

        new("spotify", "Spotify", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Spotify\\Storage", "Streaming cache"),
                new("Spotify\\Browser", "Browser cache")
            ])
        ]),

        new("teams-classic", "Microsoft Teams (classic)", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Teams\\Cache", "App cache"),
                new("Microsoft\\Teams\\Code Cache", "Code cache"),
                new("Microsoft\\Teams\\GPUCache", "GPU cache"),
                new("Microsoft\\Teams\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("vscode", "Visual Studio Code", "Desktop App", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Code\\Cache", "App cache"),
                new("Code\\CachedData", "Cached data"),
                new("Code\\Code Cache", "Code cache"),
                new("Code\\GPUCache", "GPU cache"),
                new("Code\\Service Worker\\CacheStorage", "Service worker cache"),
                new("Code\\logs", "Log files")
            ])
        ]),

        new("chrome", "Google Chrome", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Google\\Chrome\\User Data\\Default\\Cache", "Browser cache"),
                new("Google\\Chrome\\User Data\\Default\\Code Cache", "Code cache"),
                new("Google\\Chrome\\User Data\\Default\\GPUCache", "GPU cache"),
                new("Google\\Chrome\\User Data\\Default\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("brave", "Brave", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache", "Browser cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Code Cache", "Code cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\GPUCache", "GPU cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Service Worker\\CacheStorage", "Service worker cache")
            ])
        ]),

        new("adobe-acrobat", "Adobe Acrobat", "Desktop App", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Adobe\\Acrobat\\DC\\Cache", "Document cache"),
                new("Adobe\\Acrobat\\DC\\Temp", "Temp files")
            ])
        ]),

        // ---- Microsoft Store Apps ----
        new("store-bing-news", "Bing News", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-bing-weather", "Bing Weather", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-clipchamp", "Clipchamp - Video Editor", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\AC\\INetCache", "Internet cache"),
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\AC\\Temp", "Temp files"),
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\TempState", "Temp state")
            ])
        ]),

        new("store-media-player", "Media Player", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-photos", "Microsoft Photos", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-sticky-notes", "Microsoft Sticky Notes", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-todo", "Microsoft To Do", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-snip-sketch", "Snip & Sketch", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-client-temp", "Windows Client Temp Files", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\TempState", "Temp state")
            ])
        ]),

        new("store-xbox", "Xbox", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-xbox-gamebar", "Xbox Game Bar", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-whatsapp", "WhatsApp", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\AC\\INetCache", "Internet cache"),
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\AC\\Temp", "Temp files"),
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\TempState", "Temp state")
            ])
        ]),

        new("store-spotify", "Spotify (Store)", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\AC\\INetCache", "Internet cache"),
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\AC\\Temp", "Temp files"),
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\TempState", "Temp state")
            ])
        ]),

        new("store-netflix", "Netflix", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\AC\\INetCache", "Internet cache"),
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\AC\\Temp", "Temp files"),
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\TempState", "Temp state")
            ])
        ]),

        new("store-paint", "Paint", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-notepad", "Windows Notepad", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ]),

        new("store-terminal", "Windows Terminal", "Microsoft Store App", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\AC\\INetCache", "Internet cache"),
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\AC\\Temp", "Temp files"),
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\TempState", "Temp state")
            ])
        ])
    ];

    private static readonly Dictionary<string, AppDefinition> ById =
        Definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public static AppDefinition? Find(string id)
        => ById.TryGetValue(id, out var def) ? def : null;

    public static IReadOnlyList<AppDefinition> ForGroup(string group)
        => Definitions.Where(d => d.Group == group).ToArray();

    public static IReadOnlyList<string> Groups()
        => Definitions.Select(d => d.Group).Distinct().ToArray();
}
