using System.Text.Json;
using System.Text.Json.Nodes;
using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

[Collection("Cleaning")]
public sealed class AuditFollowupTests
{
    [Fact]
    public void StaleSettingsOnlyApplyLocalEditsIncludingIndividualMapAndScheduleEntries()
    {
        var baseline = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject();
        baseline["Schedules"] = JsonNode.Parse("[{\"Id\":\"first\",\"Name\":\"original\",\"Hour\":3}]");
        var page = baseline.DeepClone().AsObject();
        var latest = baseline.DeepClone().AsObject();
        latest["CleanAtStartup"] = true;
        latest["BrowserCleanupSelection"]!["edge:cache"] = false;
        latest["Schedules"]![0]!["Hour"] = 7;
        page["ShowInTaskbar"] = false;
        page["BrowserCleanupSelection"]!["chrome:cache"] = false;
        page["Schedules"]![0]!["Name"] = "renamed";
        var merged = AppSettings.MergeChanges(baseline, page, latest);
        Assert.True(merged["CleanAtStartup"]!.GetValue<bool>());
        Assert.False(merged["ShowInTaskbar"]!.GetValue<bool>());
        Assert.False(merged["BrowserCleanupSelection"]!["edge:cache"]!.GetValue<bool>());
        Assert.False(merged["BrowserCleanupSelection"]!["chrome:cache"]!.GetValue<bool>());
        Assert.Equal(7, merged["Schedules"]![0]!["Hour"]!.GetValue<int>());
        Assert.Equal("renamed", merged["Schedules"]![0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task SeparateSettingsInstancesSaveWithoutClobberingEachOther()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "settings.json");
        var a = new AppSettings { ShowInTaskbar = false };
        var b = new AppSettings { CleanAtStartup = true };
        await Task.WhenAll(a.SaveToAsync(path), b.SaveToAsync(path));
        var restored = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path))!;
        Assert.False(restored.ShowInTaskbar);
        Assert.True(restored.CleanAtStartup);
        a.CloseToTray = true;
        await a.SaveToAsync(path);
        restored = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path))!;
        Assert.True(restored.CleanAtStartup);
        Assert.True(restored.CloseToTray);
    }

    [Fact]
    public async Task SeparateGatesExcludeEachOtherAndCancellationDoesNotLeakTheLock()
    {
        using var scratch = new Scratch();
        var a = new ProcessGate("test", scratch.Root);
        var b = new ProcessGate("test", scratch.Root);
        await a.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(150);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(cts.Token));
        }
        finally { a.Release(); }
        await b.WaitAsync();
        b.Release();
        await a.WaitAsync();
        a.Release();
    }

    [Theory]
    [InlineData("[HKEY_LOCAL_MACHINE\\Software\\Test]\n\"value\"=\"text\"")]
    [InlineData("[-HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]")]
    [InlineData("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\n\"App\"=-")]
    [InlineData("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce]\n\"App\"=\"text\"")]
    [InlineData("[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\nmalformed")]
    public async Task RegistryPayloadRefusesOutOfScopeDeleteAndMalformedDirectives(string body)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Windows Registry Editor Version 5.00\n\n" + body));
        await Assert.ThrowsAsync<InvalidDataException>(() => RegistryPayload.ValidateAsync(stream,
            @"Software\Microsoft\Windows\CurrentVersion\Run"));
    }

    [Fact]
    public async Task RegistryPayloadAcceptsExportedStringsDwordsAndMultilineHex()
    {
        const string text = "Windows Registry Editor Version 5.00\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n" +
            "@=\"default\"\r\n\"App\"=\"C:\\\\Apps\\\\app.exe\"\r\n\"Count\"=dword:00000001\r\n" +
            "\"Blob\"=hex(2):01,02,\\\r\n  03,04\r\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        await RegistryPayload.ValidateAsync(stream, @"Software\Microsoft\Windows\CurrentVersion\Run");
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void MissingFileDetectionDoesNotConfuseExistingDirectoriesAndInvalidPathsWithGoneFiles()
    {
        using var scratch = new Scratch();
        Assert.False(CleanupService.IsPositivelyMissing(scratch.Root));
        Assert.False(CleanupService.IsPositivelyMissing("bad\0path"));
        Assert.True(CleanupService.IsPositivelyMissing(Path.Combine(scratch.Root, "gone.exe")));
    }

    [Fact]
    public async Task CancellationRecordsExactPartialMutationsOnceBeforeItPropagates()
    {
        var records = new List<(string, long, long)>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CleanupJournal.RunAsync("Test", async () =>
        {
            CleanupJournal.Record(1, 10);
            await Task.Run(() => CleanupJournal.Record(2, 20));
            await Task.FromCanceled(new CancellationToken(true));
            return 0;
        }, (area, items, bytes) => { records.Add((area, items, bytes)); return Task.CompletedTask; }));
        Assert.Equal(("Test", 3L, 30L), Assert.Single(records));
        await CleanupJournal.RunAsync("Success", () => Task.FromResult(1),
            (_, _, _) => throw new InvalidOperationException("A normal completion must not record twice"));
    }

    [Fact]
    public void ResourceBudgetsFailExplicitlyAndPreferenceReadsAreBounded()
    {
        var budget = new WorkBudget(maxEntries: 2, maxDepth: 1);
        budget.Check();
        budget.Check();
        Assert.Throws<IOException>(() => budget.Check());
        Assert.Throws<IOException>(() => new WorkBudget(maxDepth: 1).Check(2));
        var bytes = new WorkBudget(maxBytes: 4);
        bytes.ConsumeBytes(4);
        Assert.Throws<IOException>(() => bytes.ConsumeBytes(1));
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.Root, "large-preferences");
        using (var file = File.Create(path)) file.SetLength(BrowserCleanupService.MaxPreferenceBytes + 1L);
        Assert.Throws<InvalidDataException>(() => BrowserCleanupService.ReadPreferenceText(path));
    }

    [Fact]
    public void SafeDeletionOnlyAcceptsOrdinaryLocalWindowsPaths()
    {
        Assert.True(SafeDeletion.IsSupportedPath(@"C:\Users\test\cache.tmp"));
        Assert.False(SafeDeletion.IsSupportedPath(@"\\server\share\cache.tmp"));
        Assert.False(SafeDeletion.IsSupportedPath(@"C:\cache.tmp:secret"));
        Assert.False(SafeDeletion.IsSupportedPath(@"\\?\C:\cache.tmp"));
    }

    [Fact]
    public void AppFileDrilldownStopsBeforeReadingTheEntireTree()
    {
        using var scratch = new Scratch();
        for (var i = 0; i < 210; i++) File.WriteAllText(Path.Combine(scratch.Root, i + ".tmp"), "x");
        var files = AppCleanupService.ListItemFiles(scratch.Root, 5);
        Assert.Equal(5, files.Count);
        Assert.All(files, file => Assert.Equal(1, file.Bytes));
    }

    internal sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cm-followup-" + Guid.NewGuid().ToString("N"));
        internal Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
