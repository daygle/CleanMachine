using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the trust boundary around registry restore points.
/// <para>
/// A <c>.reg</c> file is a script, and the backup directories include %TEMP%, so
/// "a .reg file in the backups folder" is not evidence that CleanMachine wrote it.
/// These tests pin the two gates that fix that: a restore point must be in the
/// app's own record of what it exported, and its recorded key root must be one the
/// cleaner is allowed to touch at all.
/// </para>
/// <para>
/// They are kept out of the "Cleaning" collection on purpose: none of them touch
/// the registry or the shared cleaning counter, and they should not be made to
/// wait behind real cleaning work.</para></summary>
public sealed class BackupProvenanceTests
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunRoot = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "cm-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A minimal but genuinely valid restore point: reg.exe's header
    /// line is what ValidateBackupAsync checks, so a test file needs one.</summary>
    private static string WriteRegFile(string directory, string name, string body = "")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path,
            "Windows Registry Editor Version 5.00\r\n\r\n" +
            "[-HKEY_CURRENT_USER\\" + UninstallRoot + "]\r\n" + body);
        return path;
    }

    [Fact]
    public void AFileTheAppNeverWroteHasNoProvenance()
    {
        var scratch = Scratch();
        try
        {
            // The planted-file case: a plausible name, a plausible date, and a
            // valid reg header - and no record of it anywhere.
            var planted = WriteRegFile(scratch, "registry-uninstall-20991231-235959.reg");

            var match = BackupProvenance.Match(planted, BackupProvenance.Load());

            Assert.Null(match);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ARecordedBackupMatchesUntilItsBytesChange()
    {
        var scratch = Scratch();
        try
        {
            var manifest = Path.Combine(scratch, "provenance.json");
            var backup = WriteRegFile(scratch, "registry-run-20260101-010000.reg");
            var created = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

            BackupProvenance.Record(manifest, backup, RunRoot, created);

            var match = BackupProvenance.Match(backup, BackupProvenance.Load(manifest));
            Assert.NotNull(match);
            Assert.Equal(RunRoot, match!.KeyRoot, ignoreCase: true);
            Assert.Equal(created, match.CreatedAtUtc);

            // Same name, different bytes: a real restore point whose content was
            // swapped after export must stop matching.
            File.WriteAllText(backup, "Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_CURRENT_USER\\Evil]\r\n");
            Assert.Null(BackupProvenance.Match(backup, BackupProvenance.Load(manifest)));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RecordingTheSameNameTwiceReplacesRatherThanDuplicates()
    {
        var scratch = Scratch();
        try
        {
            var manifest = Path.Combine(scratch, "provenance.json");
            var backup = WriteRegFile(scratch, "registry-run-20260101-010000.reg");

            BackupProvenance.Record(manifest, backup, RunRoot, DateTimeOffset.UtcNow);
            BackupProvenance.Record(manifest, backup, UninstallRoot, DateTimeOffset.UtcNow);

            var entries = BackupProvenance.Load(manifest);
            var entry = Assert.Single(entries);
            Assert.Equal(UninstallRoot, entry.KeyRoot, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AMalformedManifestReadsAsNothingProvenanced()
    {
        var scratch = Scratch();
        try
        {
            var manifest = Path.Combine(scratch, "provenance.json");
            File.WriteAllText(manifest, "{ this is not json");

            // Fails safe: an unreadable record must not become a permissive one.
            Assert.Empty(BackupProvenance.Load(manifest));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RestoreRefusesAFileTheAppNeverExported()
    {
        var scratch = Scratch();
        try
        {
            var planted = WriteRegFile(scratch, "registry-uninstall-20260101-010000.reg");
            var backup = new RegistryBackup(planted, DateTimeOffset.UtcNow);

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => RegistryCareService.RestoreBackupAsync(backup));

            Assert.Contains("not a restore point CleanMachine created", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ListBackupsMarksOnlyProvenancedFilesAsRestorable()
    {
        var scratch = Scratch();
        try
        {
            var manifest = Path.Combine(scratch, "provenance.json");
            var real = WriteRegFile(scratch, "registry-run-20260101-010000.reg");
            var planted = WriteRegFile(scratch, "registry-uninstall-20991231-235959.reg");
            BackupProvenance.Record(manifest, real, RunRoot, new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));

            var provenance = BackupProvenance.Load(manifest);
            var listed = RegistryCareService.ListBackups(scratch, provenance);

            // Both are still visible - the user can see and delete a stray file -
            // but only the one the app can account for is restorable.
            Assert.Equal(2, listed.Count);
            var verified = Assert.Single(listed, b => b.Verified);
            Assert.Equal(real, verified.FilePath);
            Assert.Equal(RunRoot, verified.KeyRoot, ignoreCase: true);

            var unverified = Assert.Single(listed, b => !b.Verified);
            Assert.Equal(planted, unverified.FilePath);
            Assert.Equal(string.Empty, unverified.KeyRoot);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestorableScopesAreTheOnesTheCleanerActuallyExports()
    {
        // Every scope CreateBackupsAsync can produce must be restorable...
        foreach (var scope in new[]
                 {
                     UninstallRoot,
                     @"Software\Classes\.txt",
                     @"Control Panel\Desktop\MuiCached",
                     RunRoot,
                     @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                     @"AppEvents\Schemes\Apps\Notifications",
                     @"Software\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt",
                     @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Installer"
                 })
        {
            Assert.True(RegistryCareService.IsRestorableScope(scope), scope);
        }
    }

    [Fact]
    public void RestorableScopesIncludeAnythingBeneathAnAllowedRoot()
    {
        // A restore writes values rather than deleting keys, so a sub-key of an
        // area the cleaner already operates in stays inside the boundary. The
        // Uninstall root itself is here for the same reason: it is exported whole.
        Assert.True(RegistryCareService.IsRestorableScope(@"Software\Microsoft\Windows\CurrentVersion\Run\Sub"));
    }

    [Fact]
    public void RestorableScopesExcludeEverywhereElse()
    {
        // Restore writes whatever the file contains, so it is bounded by the same
        // allow-list as deletion - not a wider one.
        foreach (var scope in new[]
                 {
                     null,
                     "",
                     "   ",
                     @"Software\Classes\CLSID\{0}",                            // not a per-user extension key
                     @"Software\Classes\.txt\Shell",                           // below a per-user extension key
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
                     @"Software\Classes\Wow6432Node",
                     @"System\CurrentControlSet\Services",
                     @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
                 })
        {
            Assert.False(RegistryCareService.IsRestorableScope(scope), scope ?? "<null>");
        }
    }

    [Fact]
    public void AnOversizedManifestIsIgnoredRatherThanParsed()
    {
        var scratch = Scratch();
        try
        {
            var manifest = Path.Combine(scratch, "provenance.json");
            File.WriteAllBytes(manifest, new byte[BackupProvenance.MaxManifestBytes + 1]);

            Assert.Empty(BackupProvenance.Load(manifest));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TheRecycleBinMetadataHeaderIsReadForTheRealDeletionTime()
    {
        var scratch = Scratch();
        try
        {
            // Version 2 layout: 8-byte version, 8-byte size, 8-byte FILETIME.
            var deleted = new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc);
            var path = Path.Combine(scratch, "$Iabc123.reg");
            var bytes = new byte[24];
            BitConverter.GetBytes(2).CopyTo(bytes, 0);            // version 2
            BitConverter.GetBytes(1024L).CopyTo(bytes, 8);        // original size
            BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(bytes, 16);
            File.WriteAllBytes(path, bytes);

            Assert.Equal(deleted, RecycleBinService.ReadDeletionTimeUtc(path));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AnUnreadableRecycleBinMetadataHeaderYieldsNoTime()
    {
        var scratch = Scratch();
        try
        {
            var truncated = Path.Combine(scratch, "$Itruncated");
            File.WriteAllBytes(truncated, new byte[8]);
            Assert.Null(RecycleBinService.ReadDeletionTimeUtc(truncated));

            var absent = Path.Combine(scratch, "$Imissing");
            Assert.Null(RecycleBinService.ReadDeletionTimeUtc(absent));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LoadedSettingsAreClampedBeforeAnythingActsOnThem()
    {
        var settings = new AppSettings
        {
            SystemMonitorFreeSpaceGb = double.NaN,
            SystemMonitorFreeSpaceUnit = "parsecs",
            SystemMonitorAction = (ExitAction)99,
            IdleCleanMinutes = -50,
            RecycleBinAutoEmptyDays = 0,
            ExcludedPaths = null!,
            QuickCleanWindowsCategories = null,
            Schedules =
            [
                new CleanupSchedule { Id = "abc123", Hour = 99, Minute = 77, DayOfMonth = 400, Trigger = (ScheduleTrigger)42, AfterClean = (ScheduleAction)42 },
                new CleanupSchedule { Id = "bad id with spaces & symbols", Name = "" },
                new CleanupSchedule { Id = "-_" }
            ]
        };

        var sanitized = settings.Sanitize();

        Assert.Equal(1.0, sanitized.SystemMonitorFreeSpaceGb);
        Assert.Equal("GB", sanitized.SystemMonitorFreeSpaceUnit);
        Assert.Equal(ExitAction.CleanAndNotify, sanitized.SystemMonitorAction);
        Assert.Equal(15, sanitized.IdleCleanMinutes);
        Assert.Equal(30, sanitized.RecycleBinAutoEmptyDays);
        Assert.NotNull(sanitized.ExcludedPaths);
        Assert.NotNull(sanitized.QuickCleanWindowsCategories);

        // A schedule id is embedded in a task name and a cmd.exe line, so it is
        // filtered by the same rule the builders enforce.
        var kept = Assert.Single(sanitized.Schedules);
        Assert.Equal("abc123", kept.Id);
        Assert.Equal(23, kept.Hour);
        Assert.Equal(59, kept.Minute);
        Assert.Equal(31, kept.DayOfMonth);
        Assert.Equal(ScheduleTrigger.Weekly, kept.Trigger);
        Assert.Equal(ScheduleAction.Nothing, kept.AfterClean);
        Assert.Equal("Scheduled cleanup", kept.Name);
    }

    [Fact]
    public void SettingsSanitizingIsIdempotentAndLeavesValidValuesAlone()
    {
        var settings = new AppSettings
        {
            SystemMonitorFreeSpaceGb = 5.5,
            IdleCleanMinutes = 30,
            RecycleBinAutoEmptyDays = 7,
            Schedules = [new CleanupSchedule { Id = "keep-me", Hour = 3, Minute = 30 }]
        };

        var once = settings.Sanitize();
        var twice = once.Sanitize();

        Assert.Equal(5.5, once.SystemMonitorFreeSpaceGb);
        Assert.Equal(30, once.IdleCleanMinutes);
        Assert.Equal(7, once.RecycleBinAutoEmptyDays);
        Assert.Equal(3, twice.Schedules[0].Hour);
        Assert.Equal(30, twice.Schedules[0].Minute);
    }

    [Fact]
    public void TheScheduleIdRuleIsSharedBetweenSettingsAndTheCommandBuilders()
    {
        Assert.True(ScheduledTask.IsValidScheduleId("abc123"));
        Assert.True(ScheduledTask.IsValidScheduleId("a-b_c"));
        Assert.False(ScheduledTask.IsValidScheduleId("has space"));
        Assert.False(ScheduledTask.IsValidScheduleId("quote\""));
        Assert.False(ScheduledTask.IsValidScheduleId("amp&and|pipe"));
        Assert.False(ScheduledTask.IsValidScheduleId(""));
        Assert.False(ScheduledTask.IsValidScheduleId(null));
        Assert.False(ScheduledTask.IsValidScheduleId(new string('a', 65)));
    }
}
