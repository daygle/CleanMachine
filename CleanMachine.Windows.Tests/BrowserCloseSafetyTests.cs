using System.Text.RegularExpressions;
using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins that CleanMachine never closes a browser someone is using: only
/// the manual Browser Cleaner flow may close one (after the user confirms), only the
/// browsers being cleaned, and never one that still has a window open. Read-only:
/// nothing here starts, closes or inspects a real process.</summary>
public sealed class BrowserCloseSafetyTests
{
    [Fact]
    public void ABrowserWithAnOpenWindowIsNeverForceClosed()
    {
        // An open window after the polite close means the browser is in use - most
        // often a "Leave site? Changes may not be saved" prompt.
        Assert.False(BrowserCleanupService.MayEndWithoutLosingWork(hasOpenWindow: true));
        // Windowless background leftovers (Chrome background apps, Edge Startup
        // Boost) are the only thing ever ended.
        Assert.True(BrowserCleanupService.MayEndWithoutLosingWork(hasOpenWindow: false));
    }

    [Fact]
    public void OnlyTheBrowsersBeingCleanedAreConsidered()
    {
        var edgeOnly = BrowserCleanupService.ProcessNamesFor(["edge"]);
        Assert.Equal(new[] { "msedge" }, edgeOnly);

        var chromeAndFirefox = BrowserCleanupService.ProcessNamesFor(["chrome", "firefox", "not-a-browser"]);
        Assert.Contains("chrome", chromeAndFirefox);
        Assert.Contains("firefox", chromeAndFirefox);
        Assert.DoesNotContain("msedge", chromeAndFirefox);
        Assert.Empty(BrowserCleanupService.ProcessNamesFor([]));
    }

    [Fact]
    public void OnlyTheManualBrowserCleanerPageCanCloseABrowser()
    {
        // Quick Clean, schedules, the automatic triggers and close-cleaning run with
        // nobody watching; none of them may ever close a browser.
        var callers = SourceFiles()
            .Where(f => Path.GetFileName(f) != "BrowserCleanupService.cs"
                        && File.ReadAllText(f).Contains("CloseRunningBrowsersAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "CleanerPage.xaml.cs" }, callers);
    }

    [Fact]
    public void UnattendedCleansNeverRequireBrowsersToBeClosed()
    {
        // Every browser clean outside the manual page passes requireBrowsersClosed:
        // false, so it works around an open browser (cache only, recent files
        // skipped) instead of refusing - and nothing nudges toward closing one.
        foreach (var file in SourceFiles().Where(f => Path.GetFileName(f) is "App.xaml.cs" or "QuickCleanService.cs" or "ScheduleService.cs"))
        {
            var source = File.ReadAllText(file);
            var calls = Regex.Matches(source, @"CleanItemsAsync\((?<args>[^;]*);", RegexOptions.Singleline);
            Assert.NotEmpty(calls);
            Assert.All(calls, call =>
                Assert.Contains("requireBrowsersClosed: false", call.Groups["args"].Value, StringComparison.Ordinal));
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = FindRepoRoot();
        Assert.True(root is not null, "Repo root was not found above the test output directory.");
        return Directory.EnumerateFiles(Path.Combine(root!, "CleanMachine.Windows"), "*.cs", SearchOption.TopDirectoryOnly);
    }

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
