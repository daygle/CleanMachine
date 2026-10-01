using CleanMachine.Windows;
using Microsoft.Win32;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins the safety rules for the cleanup coverage added in one batch:
/// clipboard history and the internet cookie store (Windows Cleanup), the new
/// application and developer-tool caches (Application Cleanup), and the font,
/// right-click menu, COM and PATH checks (Registry Care). Read-only: nothing here
/// touches the real registry or deletes a file.</summary>
public sealed class CleanupCoverageTests
{
    // ---- Windows Cleanup ----

    [Theory]
    [InlineData("system-clipboard-history")]
    [InlineData("system-inet-cookies")]
    public void NewWindowsItemsAreReviewTierAndOffByDefault(string id)
    {
        // Clipboard history may hold something not yet pasted, and the cookie
        // store keeps apps signed in: neither may be cleaned unattended.
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == id);

        Assert.Equal(CleanupRisk.Review, category.Risk);
        Assert.False(category.EnabledByDefault);
        Assert.False(QuickCleanService.IsWindowsSelected(category, new AppSettings()));
    }

    [Fact]
    public void ClipboardHistoryIsAnActionWithAPreview()
    {
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == "system-clipboard-history");
        var preview = new WindowsCleanupService().BuildPreview([category]);

        Assert.True(category.Kind.IsAction());
        Assert.Equal(1, preview.TotalItems);
        Assert.Equal("Clear the clipboard history", Assert.Single(preview.Items).Description);
    }

    [Fact]
    public void OnlyTheDnsFlushAndClipboardAreActions()
    {
        Assert.Equal(
            new[] { CleanupKind.DnsCache, CleanupKind.ClipboardHistory },
            Enum.GetValues<CleanupKind>().Where(k => k.IsAction()).ToArray());
    }

    [Fact]
    public void CookieStoreIsInsideLocalAppData()
    {
        var category = WindowsCleanupService.Catalog.Single(c => c.Id == "system-inet-cookies");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(CleanupKind.Files, category.Kind);
        Assert.True(NativeSafety.IsWithin(category.Path!, local));
    }

    // ---- Application Cleanup ----

    /// <summary>A desktop app with no case in IsInstalled falls through to
    /// "not installed" and is silently never offered - the failure is invisible,
    /// so every desktop definition is checked for its detection case.</summary>
    [Fact]
    public void EveryDesktopAppHasAnInstallCheck()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");
        var source = File.ReadAllText(Path.Combine(root!, "CleanMachine.Windows", "AppCleanupService.cs"));

        foreach (var def in AppCatalog.Definitions.Where(d => !d.IsStoreApp))
            Assert.True(source.Contains($"\"{def.Id}\" =>", StringComparison.Ordinal),
                $"'{def.Id}' has no install check in AppCleanupService.IsInstalled.");
    }

    /// <summary>Application Cleanup items are all selected by default and swept by
    /// Quick Clean and schedules, so each must be a cache or log - never the folder
    /// that holds a session, settings, or installed packages.</summary>
    [Theory]
    [InlineData("telegram", "tdata")]
    [InlineData("telegram", "user_data")]
    [InlineData("ea-app", "CEF")]
    [InlineData("ea-app", "BrowserCache")]
    [InlineData("obs-studio", "basic")]
    [InlineData("obsidian", "obsidian.json")]
    [InlineData("nuget", "packages")]
    [InlineData("rust-cargo", "registry")]
    [InlineData("rust-cargo", "src")]
    [InlineData("gradle", "caches")]
    [InlineData("gradle", "modules-2")]
    [InlineData("webview2", "EBWebView")]
    [InlineData("webview2", "Default")]
    [InlineData("store-teams", "EBWebView")]
    [InlineData("store-outlook", "EBWebView")]
    public void NewAppEntriesNeverOfferSessionsSettingsOrPackages(string appId, string leaf)
    {
        var app = AppCatalog.Definitions.Single(d => d.Id == appId);
        var paths = app.TempLocations.SelectMany(l => l.Entries).Select(e => e.RelativePath);

        Assert.DoesNotContain(paths, p =>
            p.Split('\\', '/').Last().Equals(leaf, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("webview2")]
    [InlineData("store-teams")]
    [InlineData("store-outlook")]
    public void WebViewEntriesOnlyReachDiskCaches(string appId)
    {
        // A WebView2 profile also holds cookies and local storage; only its three
        // disk-cache folders are recreatable.
        var webView = AppCatalog.Definitions.Single(d => d.Id == appId)
            .TempLocations.SelectMany(l => l.Entries)
            .Select(e => e.RelativePath)
            .Where(p => p.Contains("EBWebView", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(webView);
        Assert.All(webView, p => Assert.Contains(p.Split('\\').Last(), new[] { "Cache", "Code Cache", "GPUCache" }));
    }

    [Fact]
    public void DeveloperCachesHaveTheirOwnGroup()
    {
        var dev = AppCatalog.Definitions.Where(d => d.Group == "Developer Tools").Select(d => d.Id).ToHashSet();

        Assert.All(new[] { "nodejs", "python-pip", "nuget", "go", "rust-cargo", "gradle" }, id => Assert.Contains(id, dev));
        Assert.Contains("Developer Tools", AppCatalog.Groups());
    }

    // ---- Registry Care ----

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" ""%1""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe %1", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe -min", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\a.exe.d\b.exe %1", @"C:\a.exe.d\b.exe")]    // ".exe" inside a folder name
    [InlineData(@"C:\App\app.exe", @"C:\App\app.exe")]
    [InlineData(@"C:\Tools\run.bat", @"C:\Tools\run.bat")]       // no spaces: taken whole
    [InlineData(@"C:\Program Files\App\run.cmd %1", null)]      // spaces and no .exe: ambiguous
    [InlineData(@"app.exe %1", null)]                           // not fully qualified
    [InlineData(@"""%ProgramFiles%\App\app.exe""", null)]       // environment variable
    [InlineData(@"""C:\App\app.exe", null)]                     // unterminated quote
    [InlineData("", null)]
    public void UnquotedPathsWithSpacesResolveToTheWholePath(string command, string? expected)
    {
        // Cutting at the first space read "C:\Program" as a missing program, so a
        // working startup, uninstall or menu entry could be flagged and deleted.
        Assert.Equal(expected, CleanupService.ResolveStartupExecutable(command));
        // The Startup Apps page shares the resolver, so it agrees on every case.
        Assert.Equal(expected, StartupAppsService.ResolveExecutable(command));
    }

    [Theory]
    [InlineData("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", true)]
    [InlineData("{0}", false)]
    [InlineData("86ca1aa0-34aa-4e8b-a509-50c905bae2a2", false)]
    [InlineData("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32", false)]
    [InlineData("", false)]
    public void ComKeysMustBeExactGuids(string name, bool expected)
        => Assert.Equal(expected, CleanupService.IsGuidKeyName(name));

    [Fact]
    public void ComClassIsFlaggedOnlyWhenEveryServerIsPositivelyMissing()
    {
        // Deleting the class removes every server it registers, so one live,
        // empty or unresolvable server keeps the whole class.
        const string gone = @"C:\Gone\server.dll";
        const string live = @"C:\Live\server.exe";
        Func<string, bool> exists = f => f.Equals(live, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(gone, CleanupService.MissingComServer([(false, gone)], exists));
        Assert.Equal(gone, CleanupService.MissingComServer([(false, gone), (true, @"""C:\Gone\other.exe"" /automation")], exists));
        Assert.Null(CleanupService.MissingComServer([(false, gone), (true, live)], exists));       // one live server
        Assert.Null(CleanupService.MissingComServer([(false, gone), (true, "")], exists));         // deliberate empty override
        Assert.Null(CleanupService.MissingComServer([(false, ""), (true, @"C:\Gone\x.exe")], exists));
        Assert.Null(CleanupService.MissingComServer([(false, gone), (false, null)], exists));     // value absent
        Assert.Null(CleanupService.MissingComServer([(false, gone), (false, "mscoree.dll")], exists)); // unresolvable
        Assert.Null(CleanupService.MissingComServer([(false, @"%SystemRoot%\x.dll")], exists));
        Assert.Null(CleanupService.MissingComServer([], exists));                                  // no servers at all
    }

    [Fact]
    public void DelegatedRightClickVerbsAreNeverFlagged()
    {
        // Explorer runs a DelegateExecute / ExplorerCommandHandler COM handler and
        // ignores the command string, so a stale string there is not a broken entry
        // - and Context Menu is a Quick Clean category, so a false flag would delete
        // a working verb. Uses a scratch key, never a real shell registration.
        const string scratch = @"Software\CleanMachineShellVerbTest\shell";
        var missing = @"""C:\definitely-missing-xyz\app.exe"" ""%1""";
        var live = $@"""{Path.Combine(Environment.SystemDirectory, "cmd.exe")}"" /c";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            using (var shell = root.CreateSubKey(scratch))
            {
                using (var c = shell.CreateSubKey(@"Broken\command")) c.SetValue("", missing);
                using (var c = shell.CreateSubKey(@"Delegated\command"))
                {
                    c.SetValue("", missing);
                    c.SetValue("DelegateExecute", "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}");
                }
                using (var v = shell.CreateSubKey("Handler"))
                {
                    v.SetValue("ExplorerCommandHandler", "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}");
                    using var c = v.CreateSubKey("command");
                    c.SetValue("", missing);
                }
                using (var c = shell.CreateSubKey(@"Live\command")) c.SetValue("", live);
            }

            var findings = new List<RegistryFinding>();
            using (var shell = root.OpenSubKey(scratch)!)
                CleanupService.ScanShellVerbs(shell, scratch, findings);

            var flagged = Assert.Single(findings);
            Assert.Equal($@"{scratch}\Broken", flagged.Path);
            Assert.Equal("Context Menu", flagged.Category);
        }
        finally
        {
            root.DeleteSubKeyTree(@"Software\CleanMachineShellVerbTest", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void MissingPathFoldersReportsOnlyVerifiableMissingEntries()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools" };
        var raw = @"C:\Tools; C:\Gone ;""C:\Quoted Gone"";;relative\bin;\\server\share;%NOT_A_REAL_VAR_XYZ%\bin;C:\Gone";

        var missing = CleanupService.MissingPathFolders(raw, existing.Contains);

        // Present, empty, relative, UNC (may be offline) and unexpandable entries
        // are never reported; a duplicate is reported once.
        Assert.Equal(new[] { @"C:\Gone", @"C:\Quoted Gone" }, missing);
    }

    [Theory]
    [InlineData(@"Software\Classes\*\shell\OpenWithGone", true)]
    [InlineData(@"Software\Classes\Directory\Background\shell\Terminal", true)]
    [InlineData(@"Software\Classes\Directory\shell\Scan", true)]
    [InlineData(@"Software\Classes\Drive\shell\Scan", true)]
    [InlineData(@"Software\Classes\*\shell\Verb\command", false)]      // deeper than the verb
    [InlineData(@"Software\Classes\*\shell", false)]                   // the shell key itself
    [InlineData(@"Software\Classes\exefile\shell\open", false)]        // not a catch-all type
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", true)]
    [InlineData(@"Software\Classes\CLSID\{0}", false)]
    [InlineData(@"Software\Classes\CLSID", false)]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", false)]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts", true)]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts\Sub", false)]
    [InlineData(@"Environment", false)]
    public void NewRegistryScopesAreExact(string path, bool deletable)
        => Assert.Equal(deletable, RegistryCareService.IsDeletablePath(path));

    [Theory]
    [InlineData(@"Software\Classes\*\shell\OpenWithGone")]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts")]
    public void NewRegistryScopesCanBeRestored(string scope)
        => Assert.True(RegistryCareService.IsRestorableScope(scope));

    [Fact]
    public void AFontFindingMustNameItsValue()
    {
        // Fonts is one shared key; without a value name the cleaner would delete
        // the key itself, unregistering every per-user font.
        var key = new RegistryFinding("HKCU", CleanupService.UserFontsKey, "test", true, 75, "Fonts");
        var value = key with { ValueName = "Gone Font (TrueType)" };

        Assert.False(RegistryCareService.IsCleanable(key));
        Assert.True(RegistryCareService.IsCleanable(value));
    }

    [Fact]
    public void PathFindingsAreNeverCleanable()
    {
        // Shaped exactly as the scanner reports them. PATH is a single shared
        // value, so these are report-only: low risk is false and the Environment
        // key is outside the deletion allow-list, so either alone refuses them.
        var finding = new RegistryFinding("HKCU", CleanupService.UserEnvironmentKey,
            "PATH lists a folder that no longer exists", false, 40, "PATH (Report Only)", @"C:\Gone");

        Assert.False(RegistryCareService.IsCleanable(finding));
        Assert.False(RegistryCareService.IsCleanable(finding with { LowRisk = true, Confidence = 100 }));
        Assert.DoesNotContain(finding.Category, QuickCleanService.RegistryCategories);
    }

    [Fact]
    public void ComRegistrationsStayOutOfQuickClean()
    {
        // Removing a COM class is reviewed by a person on the Registry Care page,
        // never done unattended; fonts and menu entries are plain dead references.
        Assert.DoesNotContain("COM Registrations", QuickCleanService.RegistryCategories);
        Assert.Contains("Fonts", QuickCleanService.RegistryCategories);
        Assert.Contains("Context Menu", QuickCleanService.RegistryCategories);
    }

    [Theory]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts", "Gone (TrueType)", "Font: Gone (TrueType)")]
    [InlineData(@"Software\Classes\*\shell\OpenWithGone", null, "Right-click entry: OpenWithGone")]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", null, "COM class: {86ca1aa0-34aa-4e8b-a509-50c905bae2a2}")]
    [InlineData("Environment", @"C:\Gone", @"Missing PATH folder: C:\Gone")]
    public void NewFindingsHaveReadableNames(string path, string? valueName, string expected)
        => Assert.Equal(expected, RegistryCareService.DisplayName(
            new RegistryFinding("HKCU", path, "test", true, 70, "Test", valueName)));

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
