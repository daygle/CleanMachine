namespace CleanMachine.Windows;

/// <summary>Where an app stores its data relative to a root.</summary>
public enum AppDataRoot
{
    LocalAppData,
    RoamingAppData,
    ProgramFiles,
    ProgramFilesX86,
    UserProfile,
    WindowsTemp,
    /// <summary>%USERPROFILE%\AppData\LocalLow, used by low-integrity apps (Java).</summary>
    LocalLowAppData
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
        // ---- Desktop Applications ----
        new("7zip", "7-Zip", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("7-Zip\\Temp", "Extraction Temp Files"),
                new("7-Zip\\History", "File History")
            ])
        ]),

        new("github-desktop", "GitHub Desktop", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("GitHub Desktop\\Cache", "Application Cache"),
                new("GitHub Desktop\\Code Cache", "Code Cache"),
                new("GitHub Desktop\\GPUCache", "GPU Cache"),
                new("GitHub Desktop\\logs", "Log Files"),
                new("GitHub Desktop\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("notepadpp", "Notepad++", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                // Not "backup" or "session.xml": the backup folder holds the
                // contents of unsaved tabs and the session file is the list of open
                // tabs. Neither is a cache, and deleting either loses user work.
                new("Notepad++\\cache", "Cache Files")
            ])
        ]),

        new("steam", "Steam", "Desktop Application", false,
        [
            (AppDataRoot.ProgramFilesX86, [
                new("Steam\\appcache", "Application Cache"),
                new("Steam\\logs", "Log Files"),
                new("Steam\\config\\htmlcache", "HTML Cache"),
                new("Steam\\dumps", "Crash Dumps")
            ]),
            (AppDataRoot.LocalAppData, [
                new("Steam\\htmlcache", "HTML Cache")
            ])
        ]),

        new("vlc", "VLC Media Player", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("vlc\\cache", "Cache Files"),
                new("vlc\\logs", "Log Files")
            ])
        ]),

        new("zoom", "Zoom", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                // Not "data": it holds Zoom's local settings database, so clearing
                // it signs the user out and resets their preferences.
                new("Zoom\\logs", "Log Files"),
                new("Zoom\\Updates", "Old Update Files")
            ])
        ]),

        new("microsoft-office", "Microsoft Office", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Office\\16.0\\OfficeFileCache", "Office File Cache"),
                new("Microsoft\\Office\\16.0\\Wef\\Cache", "Workflow Cache"),
                new("Microsoft\\Office\\16.0\\OfficeNotification", "Notification Cache")
            ]),
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Office\\Recent", "Recent Documents")
            ])
        ]),

        new("onedrive", "Microsoft OneDrive", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("OneDrive\\logs", "Log Files"),
                new("OneDrive\\cache", "Cache Files"),
                new("OneDrive\\Updates", "Old Update Files")
            ])
        ]),

        new("microsoft-edge", "Microsoft Edge", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Edge\\User Data\\Default\\Cache", "Browser Cache"),
                new("Microsoft\\Edge\\User Data\\Default\\Code Cache", "Code Cache"),
                new("Microsoft\\Edge\\User Data\\Default\\GPUCache", "GPU Cache"),
                new("Microsoft\\Edge\\User Data\\Default\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("activity-history", "Activity History", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("ConnectedDevicesPlatform", "Activity History"),
                new("Microsoft\\Windows\\ActivityCache", "Activity Cache")
            ])
        ]),

        new("defender", "Windows Defender", "Desktop Application", false,
        [
            (AppDataRoot.WindowsTemp, [
                new("MpCmdRun.log", "Defender Scan Log"),
                new("MpScanResults-*.log", "Old Scan Results")
            ])
        ]),

        new("mediaplayer", "Windows Media Player", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Media Player\\CurrentDatabase_*.wmdb", "Old Database"),
                new("Microsoft\\Media Player\\Cache", "Cache Files")
            ])
        ]),

        new("autoplay", "Windows AutoPlay Devices", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Windows\\Autoplay", "AutoPlay Cache")
            ])
        ]),

        new("search", "Microsoft Search", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Microsoft\\Search\\Data\\Applications\\Windows\\Windows.edb", "Search Index (Will Rebuild)"),
                new("Microsoft\\Search\\Data\\Temp", "Search Temp Files")
            ])
        ]),

        new("discord", "Discord", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("discord\\Cache", "Application Cache"),
                new("discord\\Code Cache", "Code Cache"),
                new("discord\\GPUCache", "GPU Cache"),
                new("discord\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("slack", "Slack", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Slack\\Cache", "Application Cache"),
                new("Slack\\Code Cache", "Code Cache"),
                new("Slack\\GPUCache", "GPU Cache"),
                new("Slack\\Service Worker\\CacheStorage", "Service Worker Cache"),
                new("Slack\\logs", "Log Files")
            ])
        ]),

        new("spotify", "Spotify", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Spotify\\Storage", "Streaming Cache"),
                new("Spotify\\Browser", "Browser Cache")
            ])
        ]),

        new("teams-classic", "Microsoft Teams (Classic)", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Microsoft\\Teams\\Cache", "Application Cache"),
                new("Microsoft\\Teams\\Code Cache", "Code Cache"),
                new("Microsoft\\Teams\\GPUCache", "GPU Cache"),
                new("Microsoft\\Teams\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("vscode", "Visual Studio Code", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Code\\Cache", "Application Cache"),
                new("Code\\CachedData", "Cached Data"),
                new("Code\\Code Cache", "Code Cache"),
                new("Code\\GPUCache", "GPU Cache"),
                new("Code\\Service Worker\\CacheStorage", "Service Worker Cache"),
                new("Code\\logs", "Log Files")
            ])
        ]),

        new("chrome", "Google Chrome", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Google\\Chrome\\User Data\\Default\\Cache", "Browser Cache"),
                new("Google\\Chrome\\User Data\\Default\\Code Cache", "Code Cache"),
                new("Google\\Chrome\\User Data\\Default\\GPUCache", "GPU Cache"),
                new("Google\\Chrome\\User Data\\Default\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("brave", "Brave", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache", "Browser Cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Code Cache", "Code Cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\GPUCache", "GPU Cache"),
                new("BraveSoftware\\Brave-Browser\\User Data\\Default\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("adobe-acrobat", "Adobe Acrobat", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Adobe\\Acrobat\\DC\\Cache", "Document Cache"),
                new("Adobe\\Acrobat\\DC\\Temp", "Temp Files")
            ])
        ]),

        new("adobe-media-cache", "Adobe Media Cache", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Adobe\\Common\\Media Cache Files", "Media Cache Files (Premiere/After Effects)"),
                new("Adobe\\Common\\Media Cache", "Media Cache Database")
            ])
        ]),

        new("signal", "Signal", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Signal\\Cache", "Application Cache"),
                new("Signal\\Code Cache", "Code Cache"),
                new("Signal\\GPUCache", "GPU Cache"),
                new("Signal\\Service Worker\\CacheStorage", "Service Worker Cache"),
                new("Signal\\logs", "Log Files")
            ])
        ]),

        new("postman", "Postman", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Postman\\Cache", "Application Cache"),
                new("Postman\\Code Cache", "Code Cache"),
                new("Postman\\GPUCache", "GPU Cache"),
                new("Postman\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("vivaldi", "Vivaldi", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Vivaldi\\User Data\\Default\\Cache", "Browser Cache"),
                new("Vivaldi\\User Data\\Default\\Code Cache", "Code Cache"),
                new("Vivaldi\\User Data\\Default\\GPUCache", "GPU Cache"),
                new("Vivaldi\\User Data\\Default\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("opera", "Opera", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("Opera Software\\Opera Stable\\Cache", "Browser Cache"),
                new("Opera Software\\Opera Stable\\Code Cache", "Code Cache"),
                new("Opera Software\\Opera Stable\\GPUCache", "GPU Cache"),
                new("Opera Software\\Opera Stable\\Service Worker\\CacheStorage", "Service Worker Cache")
            ])
        ]),

        new("epic-games", "Epic Games Launcher", "Desktop Application", false,
        [
            (AppDataRoot.LocalAppData, [
                new("EpicGamesLauncher\\Saved\\webcache", "Web Cache"),
                new("EpicGamesLauncher\\Saved\\Logs", "Log Files")
            ])
        ]),

        new("thunderbird", "Mozilla Thunderbird", "Desktop Application", false,
        [
            // Each profile lives in a randomly-named folder, so the profile segment is a
            // wildcard. Only the disk cache is cleaned; mail and settings are untouched.
            (AppDataRoot.LocalAppData, [
                new("Thunderbird\\Profiles\\*\\cache2", "Disk Cache")
            ])
        ]),

        new("jetbrains", "JetBrains IDEs", "Desktop Application", false,
        [
            // One folder per product+version (e.g. IntelliJIdea2024.1), matched by wildcard.
            (AppDataRoot.LocalAppData, [
                new("JetBrains\\*\\caches", "IDE Caches"),
                new("JetBrains\\*\\log", "Log Files"),
                new("JetBrains\\*\\tmp", "Temp Files")
            ])
        ]),


        new("battlenet", "Battle.net", "Desktop Application", false,
        [
            // Blizzard's own troubleshooting deletes this whole folder; it holds
            // only launcher caches and logs, never game data or the sign-in.
            (AppDataRoot.LocalAppData, [
                new("Battle.net\\BrowserCaches", "Browser Cache"),
                new("Battle.net\\Cache", "Launcher Cache"),
                new("Battle.net\\Logs", "Log Files")
            ])
        ]),

        new("ea-app", "EA app", "Desktop Application", false,
        [
            // Only the embedded browser's disk caches and the logs - not the CEF
            // profile itself, which holds the sign-in.
            (AppDataRoot.LocalAppData, [
                new("Electronic Arts\\EA Desktop\\Logs", "Log Files"),
                new("Electronic Arts\\EA Desktop\\CEF\\BrowserCache\\Cache", "Browser Cache"),
                new("Electronic Arts\\EA Desktop\\CEF\\BrowserCache\\Code Cache", "Code Cache"),
                new("Electronic Arts\\EA Desktop\\CEF\\BrowserCache\\GPUCache", "GPU Cache")
            ])
        ]),

        new("telegram", "Telegram Desktop", "Desktop Application", false,
        [
            // Only the media caches inside tdata\user_data. Never tdata itself:
            // it holds the session, and deleting it signs the user out.
            (AppDataRoot.RoamingAppData, [
                new("Telegram Desktop\\tdata\\user_data\\cache", "Media Cache"),
                new("Telegram Desktop\\tdata\\user_data\\media_cache", "Streamed Media Cache")
            ])
        ]),

        new("notion", "Notion", "Desktop Application", false,
        [
            (AppDataRoot.RoamingAppData, [
                new("Notion\\Cache", "Application Cache"),
                new("Notion\\Code Cache", "Code Cache"),
                new("Notion\\GPUCache", "GPU Cache"),
                new("Notion\\Service Worker\\CacheStorage", "Service Worker Cache"),
                new("Notion\\logs", "Log Files")
            ])
        ]),

        new("figma", "Figma", "Desktop Application", false,
        [
            // Older builds keep the Electron caches at the top level, newer ones
            // inside a versioned DesktopProfile folder; both are matched.
            (AppDataRoot.RoamingAppData, [
                new("Figma\\Cache", "Application Cache"),
                new("Figma\\Code Cache", "Code Cache"),
                new("Figma\\GPUCache", "GPU Cache"),
                new("Figma\\DesktopProfile\\*\\Cache", "Application Cache"),
                new("Figma\\DesktopProfile\\*\\Code Cache", "Code Cache"),
                new("Figma\\DesktopProfile\\*\\GPUCache", "GPU Cache")
            ])
        ]),

        new("obsidian", "Obsidian", "Desktop Application", false,
        [
            // Caches only. Vaults live wherever the user put them, and
            // obsidian.json (the vault list) is left alone.
            (AppDataRoot.RoamingAppData, [
                new("obsidian\\Cache", "Application Cache"),
                new("obsidian\\Code Cache", "Code Cache"),
                new("obsidian\\GPUCache", "GPU Cache")
            ])
        ]),

        new("obs-studio", "OBS Studio", "Desktop Application", false,
        [
            // Not "basic" (scenes and profiles) or "plugin_config".
            (AppDataRoot.RoamingAppData, [
                new("obs-studio\\logs", "Log Files"),
                new("obs-studio\\crashes", "Crash Reports"),
                new("obs-studio\\profiler_data", "Profiler Data")
            ])
        ]),

        new("java", "Java", "Desktop Application", false,
        [
            (AppDataRoot.LocalLowAppData, [
                new("Sun\\Java\\Deployment\\cache", "Download Cache")
            ])
        ]),

        new("webview2", "Microsoft Edge WebView2 (App Caches)", "Desktop Application", false,
        [
            // Desktop apps that embed WebView2 keep its profile in an "EBWebView"
            // folder inside their own data folder, one or two levels under
            // LocalAppData. Only the per-profile disk caches are matched, never
            // the profile itself (cookies, local storage, sign-ins).
            (AppDataRoot.LocalAppData, [
                new("*\\EBWebView\\Default\\Cache", "WebView2 Browser Cache"),
                new("*\\EBWebView\\Default\\Code Cache", "WebView2 Code Cache"),
                new("*\\EBWebView\\Default\\GPUCache", "WebView2 GPU Cache"),
                new("*\\*\\EBWebView\\Default\\Cache", "WebView2 Browser Cache"),
                new("*\\*\\EBWebView\\Default\\Code Cache", "WebView2 Code Cache"),
                new("*\\*\\EBWebView\\Default\\GPUCache", "WebView2 GPU Cache")
            ])
        ]),

        // ---- Developer Tools ----
        // Download and build caches only, each refilled by the next install or
        // build. The installed-package folders (~\.nuget\packages,
        // ~\.cargo\registry\src, Gradle's dependency cache) are never offered.
        new("nodejs", "Node.js", "Developer Tools", false,
        [
            (AppDataRoot.LocalAppData, [
                new("npm-cache", "NPM Cache"),
                new("pnpm-store", "PNPM Store"),
                new("yarn\\Cache", "Yarn Cache")
            ])
        ]),

        new("python-pip", "Python (pip)", "Developer Tools", false,
        [
            (AppDataRoot.LocalAppData, [
                new("pip\\Cache", "Package Download Cache")
            ])
        ]),

        new("nuget", "NuGet", "Developer Tools", false,
        [
            (AppDataRoot.LocalAppData, [
                new("NuGet\\v3-cache", "HTTP Download Cache"),
                new("NuGet\\plugins-cache", "Plugin Cache")
            ])
        ]),

        new("go", "Go", "Developer Tools", false,
        [
            (AppDataRoot.LocalAppData, [
                new("go-build", "Build Cache")
            ])
        ]),

        new("rust-cargo", "Rust (Cargo)", "Developer Tools", false,
        [
            (AppDataRoot.UserProfile, [
                new(".cargo\\registry\\cache", "Downloaded Crates")
            ])
        ]),

        new("gradle", "Gradle", "Developer Tools", false,
        [
            (AppDataRoot.UserProfile, [
                new(".gradle\\caches\\build-cache-1", "Build Cache"),
                new(".gradle\\daemon\\*\\*.log", "Daemon Logs")
            ])
        ]),

        // ---- Microsoft Store Applications ----
        new("store-bing-news", "Bing News", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.BingNews_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-bing-weather", "Bing Weather", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.BingWeather_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-clipchamp", "Clipchamp - Video Editor", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\AC\\Temp", "Temp Files"),
                new("Packages\\Clipchamp.Clipchamp_yxz26nhyzhsrt\\TempState", "Temp State")
            ])
        ]),

        new("store-media-player", "Media Player", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsMediaPlayer_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-photos", "Microsoft Photos", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.Windows.Photos_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-sticky-notes", "Microsoft Sticky Notes", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-todo", "Microsoft To Do", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.Todo_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-snip-sketch", "Snip & Sketch", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.ScreenSketch_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-client-temp", "Windows Client Temp Files", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.Windows.ClientCBS_cw5n1h2txyewy\\TempState", "Temp State")
            ])
        ]),

        new("store-xbox", "Xbox", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.GamingApp_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-xbox-gamebar", "Xbox Game Bar", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-whatsapp", "WhatsApp", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\AC\\INetCache", "Internet Cache"),
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\AC\\Temp", "Temp Files"),
                new("Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\TempState", "Temp State")
            ])
        ]),

        new("store-spotify", "Spotify (Store)", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\AC\\INetCache", "Internet Cache"),
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\AC\\Temp", "Temp Files"),
                new("Packages\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\TempState", "Temp State")
            ])
        ]),

        new("store-netflix", "Netflix", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\AC\\INetCache", "Internet Cache"),
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\AC\\Temp", "Temp Files"),
                new("Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\TempState", "Temp State")
            ])
        ]),

        new("store-paint", "Paint", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.Paint_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-notepad", "Windows Notepad", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsNotepad_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-terminal", "Windows Terminal", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-teams", "Microsoft Teams", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\MSTeams_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\MSTeams_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\MSTeams_8wekyb3d8bbwe\\TempState", "Temp State"),
                // New Teams runs in WebView2; its profile folder name varies, so it
                // is a wildcard. Disk caches only, never the profile itself.
                new("Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\*\\Cache", "WebView2 Browser Cache"),
                new("Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\*\\Code Cache", "WebView2 Code Cache"),
                new("Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\*\\GPUCache", "WebView2 GPU Cache")
            ])
        ]),

        new("store-outlook", "Outlook (New)", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\TempState", "Temp State"),
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\LocalCache\\Microsoft\\Olk\\EBWebView\\*\\Cache", "WebView2 Browser Cache"),
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\LocalCache\\Microsoft\\Olk\\EBWebView\\*\\Code Cache", "WebView2 Code Cache"),
                new("Packages\\Microsoft.OutlookForWindows_8wekyb3d8bbwe\\LocalCache\\Microsoft\\Olk\\EBWebView\\*\\GPUCache", "WebView2 GPU Cache")
            ])
        ]),

        new("store-phone-link", "Phone Link", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.YourPhone_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.YourPhone_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.YourPhone_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-mail-calendar", "Mail and Calendar", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-maps", "Windows Maps", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsMaps_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsMaps_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsMaps_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-camera", "Windows Camera", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsCamera_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsCamera_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsCamera_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-solitaire", "Microsoft Solitaire Collection", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-get-help", "Get Help", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.GetHelp_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.GetHelp_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.GetHelp_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-feedback-hub", "Feedback Hub", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ]),

        new("store-widgets", "Windows Widgets", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\\AC\\INetCache", "Internet Cache"),
                new("Packages\\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\\AC\\Temp", "Temp Files"),
                new("Packages\\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\\TempState", "Temp State")
            ])
        ]),

        new("store-microsoft-store", "Microsoft Store", "Microsoft Store Application", true,
        [
            (AppDataRoot.LocalAppData, [
                new("Packages\\Microsoft.WindowsStore_8wekyb3d8bbwe\\AC\\INetCache", "Internet Cache"),
                new("Packages\\Microsoft.WindowsStore_8wekyb3d8bbwe\\AC\\Temp", "Temp Files"),
                new("Packages\\Microsoft.WindowsStore_8wekyb3d8bbwe\\TempState", "Temp State")
            ])
        ])
    ];

    private static readonly Dictionary<string, AppDefinition> ById =
        Definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public static AppDefinition? Find(string id)
        => ById.TryGetValue(id, out var def) ? def : null;

    public static IReadOnlyList<string> Groups()
        => Definitions.Select(d => d.Group).Distinct().ToArray();
}
