using System.Text.Json;
using System.Text.Json.Nodes;
using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Regression tests for defects found in the repository-wide audit:
/// exclusions typed into the Settings box, settings saves over a damaged file,
/// and in-place browser preference edits.</summary>
[Collection("Cleaning")]
public sealed class AuditRemediationTests
{
    [Theory]
    [InlineData("C:\\One\rC:\\Two")]          // what a multi-line WinUI TextBox returns
    [InlineData("C:\\One\r\nC:\\Two\r\n")]    // pasted Windows text
    [InlineData("C:\\One\n\n  C:\\Two  \n")]  // pasted Unix text, blank lines, padding
    public void EveryExcludedPathOnItsOwnLineIsKept(string text)
    {
        var paths = AppSettings.ParseExcludedPaths(text);
        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\One", paths);
        Assert.Contains(@"c:\two", paths); // case-insensitive, as the cleaners compare
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    public async Task ASettingsFileThatCannotBeReadIsReplacedOnSave(string damaged)
    {
        using var scratch = new AuditFollowupTests.Scratch();
        var path = Path.Combine(scratch.Root, "settings.json");
        await File.WriteAllTextAsync(path, damaged);

        // LoadAsync reads such a file as defaults; the save has to be able to
        // replace it, or no setting could ever be saved again.
        var settings = new AppSettings { CloseToTray = true };
        await settings.SaveToAsync(path);

        var restored = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path))!;
        Assert.True(restored.CloseToTray);
    }

    [Fact]
    public void MergeToleratesDuplicateAndMalformedIdentitiesOnDisk()
    {
        var baseline = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject();
        baseline["Schedules"] = JsonNode.Parse("[{\"Id\":\"a\",\"Name\":\"one\"}]");
        var edited = baseline.DeepClone().AsObject();
        edited["Schedules"]![0]!["Name"] = "renamed";
        var latest = baseline.DeepClone().AsObject();
        // A hand-edited file: the same id twice (differing only in case), an entry
        // whose id is not a string, and one with no id at all.
        latest["Schedules"] = JsonNode.Parse(
            "[{\"Id\":\"a\",\"Name\":\"one\"},{\"Id\":\"A\",\"Name\":\"dup\"},{\"Id\":7},{\"Name\":\"no id\"}]");

        var merged = AppSettings.MergeChanges(baseline, edited, latest);

        var schedules = merged["Schedules"]!.AsArray();
        var entry = Assert.Single(schedules);
        Assert.Equal("renamed", entry!["Name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void FirefoxPrefsKeepTheirLineEndingsAndGainNoBlankLines(string newline)
    {
        using var scratch = new AuditFollowupTests.Scratch();
        var prefs = Path.Combine(scratch.Root, "prefs.js");
        string[] kept = ["// Mozilla User Preferences", "user_pref(\"a\", 1);", "user_pref(\"b\", true);"];
        var original = string.Join(newline, [kept[0], "user_pref(\"browser.download.dir\", \"D:\\\\x\");", kept[1], kept[2]]) + newline;
        File.WriteAllText(prefs, original);
        var firefox = new BrowserDefinition("test", "Test", BrowserFamily.Firefox, [], [], [], false);

        Assert.True(BrowserCleanupService.ApplyPreferenceEdit(firefox, [scratch.Root], [], null));

        Assert.Equal(string.Join(newline, kept) + newline, File.ReadAllText(prefs));
        Assert.Equal(original, File.ReadAllText(prefs + ".cleanmachine.bak"));
        // A second pass has nothing left to remove and must not touch the file.
        Assert.False(BrowserCleanupService.ApplyPreferenceEdit(firefox, [scratch.Root], [], null));
        Assert.Equal(string.Join(newline, kept) + newline, File.ReadAllText(prefs));
    }

    [Fact]
    public void AWindowsCategoryPastTheFileCapIsReportedAndStillCleansWhatItFound()
    {
        // TrustedTemp.Root is a trusted cleanup root, so a scratch folder under it
        // can stand in for a real category.
        var root = Path.Combine(TrustedTemp.Root, "cm-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = DateTime.UtcNow.AddDays(-1);
            for (var i = 0; i < 10_001; i++)
            {
                var file = Path.Combine(root, $"{i}.tmp");
                File.WriteAllBytes(file, []);
                File.SetLastWriteTimeUtc(file, old);
            }
            var category = new CleanupCategory("test", "Test", "Test", "", CleanupRisk.Safe, true,
                CleanupKind.Files, Path: root, Pattern: "*");

            var (files, incomplete) = WindowsCleanupService.CollectCleanableFiles(category, null);

            // Used to be all-or-nothing and silent; now the found files are kept
            // and the shortfall is said out loud.
            Assert.Equal(10_000, files.Count);
            Assert.NotNull(incomplete);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ACompleteWindowsCategoryWalkReportsNothingMissing()
    {
        var root = Path.Combine(TrustedTemp.Root, "cm-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            foreach (var name in new[] { "a.tmp", Path.Combine("sub", "b.tmp") })
            {
                var file = Path.Combine(root, name);
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
            }
            var category = new CleanupCategory("test", "Test", "Test", "", CleanupRisk.Safe, true,
                CleanupKind.Files, Path: root, Pattern: "*");

            var (files, incomplete) = WindowsCleanupService.CollectCleanableFiles(category, null);

            Assert.Equal(2, files.Count);
            Assert.Null(incomplete);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProgramFilesIsOnlyCleanableThroughAProgramFilesCatalogRoot()
    {
        if (!OperatingSystem.IsWindows()) return; // protected roots are Windows folders
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(AppCleanupService.IsAllowedLocation(AppDataRoot.ProgramFilesX86, Path.Combine(programFilesX86, "Steam", "appcache")));
        // Never the Program Files root itself, never via another root, never Windows.
        Assert.False(AppCleanupService.IsAllowedLocation(AppDataRoot.ProgramFilesX86, programFilesX86));
        Assert.False(AppCleanupService.IsAllowedLocation(AppDataRoot.LocalAppData, Path.Combine(programFilesX86, "Steam", "appcache")));
        Assert.False(AppCleanupService.IsAllowedLocation(AppDataRoot.ProgramFilesX86, Path.Combine(windows, "Temp")));
        Assert.False(AppCleanupService.IsAllowedLocation(AppDataRoot.WindowsTemp, Path.Combine(windows, "Temp")));
        // Ordinary per-user locations are unaffected.
        Assert.True(AppCleanupService.IsAllowedLocation(AppDataRoot.LocalAppData,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zoom", "logs")));
    }

    [Fact]
    public void AnOversizedPreferenceFileIsSkippedNotFatal()
    {
        using var scratch = new AuditFollowupTests.Scratch();
        var path = Path.Combine(scratch.Root, "Preferences");
        using (var stream = File.Create(path))
            stream.SetLength(BrowserCleanupService.MaxPreferenceBytes + 1L);
        var chromium = new BrowserDefinition("test", "Test", BrowserFamily.Chromium, [], [], [], false);
        var issues = new List<CleanupIssue>();

        // Used to throw InvalidDataException out of the whole browser clean.
        Assert.False(BrowserCleanupService.ApplyPreferenceEdit(chromium, [scratch.Root], issues, null));
        Assert.Contains(issues, i => i.Path == path);
    }
}
