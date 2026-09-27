using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Covers the Quick Clean selection rules: which Windows categories are
/// eligible, and how the Review-risk Recycle Bin is opted in (explicit tick in
/// the Quick Clean picker only), plus what a Quick Clean run records in the
/// activity log. These tests are read-only - they exercise the selection and
/// reporting logic, never an actual Quick Clean run.</summary>
public sealed class QuickCleanSelectionTests
{
    [Fact]
    public void RecycleBinCategoryIsTheOptInEligibleReviewCategory()
    {
        // Exactly one Review-risk category is offered in the Quick Clean picker:
        // the Recycle Bin. Everything else offered stays Safe.
        var offered = WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe || c.Id == QuickCleanService.RecycleBinCategoryId)
            .ToList();

        Assert.Contains(offered, c => c.Id == QuickCleanService.RecycleBinCategoryId);
        Assert.All(offered.Where(c => c.Id != QuickCleanService.RecycleBinCategoryId),
            c => Assert.Equal(CleanupRisk.Safe, c.Risk));
    }

    [Fact]
    public void UnconfiguredSelectionFallsBackToEnabledSafeCategoriesAndNeverTheRecycleBin()
    {
        // Fresh settings: the unconfigured fallback mirrors the Windows Cleanup
        // page's enabled set, but must never sweep the Review-risk Recycle Bin in.
        var settings = new AppSettings();

        var selected = WindowsCleanupService.Catalog
            .Where(c => QuickCleanService.IsWindowsSelected(c, settings))
            .ToList();

        Assert.NotEmpty(selected);
        Assert.Contains(selected, c => c.Id == "system-temp");
        Assert.DoesNotContain(selected, c => c.Id == QuickCleanService.RecycleBinCategoryId);
    }

    [Fact]
    public void ExplicitSelectionOptInTheRecycleBin()
    {
        // Ticking the Recycle Bin in the Quick Clean picker is the opt-in; the run
        // then confirms the Review risk through ConfirmReviewCategories.
        var settings = new AppSettings
        {
            QuickCleanWindowsCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                QuickCleanService.RecycleBinCategoryId
            }
        };

        var selected = WindowsCleanupService.Catalog
            .Where(c => QuickCleanService.IsWindowsSelected(c, settings))
            .ToList();

        var bin = Assert.Single(selected);
        Assert.Equal(QuickCleanService.RecycleBinCategoryId, bin.Id);
        Assert.Equal(CleanupRisk.Review, bin.Risk);
    }

    [Fact]
    public void ExplicitSelectionOfSafeCategoriesExcludesTheRecycleBinUnlessTicked()
    {
        var settings = new AppSettings
        {
            QuickCleanWindowsCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "system-temp"
            }
        };

        var selected = WindowsCleanupService.Catalog
            .Where(c => QuickCleanService.IsWindowsSelected(c, settings))
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("system-temp", selected);
        Assert.DoesNotContain(QuickCleanService.RecycleBinCategoryId, selected);
    }

    [Fact]
    public void EmptyExplicitSelectionMeansQuickCleanCleansNothing()
    {
        // An explicitly empty picker selection is honored: no fallback, no cleaning.
        var settings = new AppSettings { QuickCleanWindowsCategories = [] };

        Assert.DoesNotContain(WindowsCleanupService.Catalog,
            c => QuickCleanService.IsWindowsSelected(c, settings));
    }

    [Fact]
    public void DisabledSafeCategoryOnWindowsCleanupPageStaysOutOfUnconfiguredFallback()
    {
        // The unconfigured fallback mirrors the Windows Cleanup page: a Safe category
        // the user disabled there is not quick-cleaned either.
        var settings = new AppSettings();
        settings.DisabledCleanupCategories.Add("system-temp");

        Assert.DoesNotContain(WindowsCleanupService.Catalog
            .Where(c => QuickCleanService.IsWindowsSelected(c, settings)),
            c => c.Id == "system-temp");
    }

    [Fact]
    public void UnconfiguredFallbackSelectionWouldRequireNoReviewConfirmation()
    {
        // The unconfigured fallback must only ever yield Safe categories, so the run
        // never needs ConfirmReviewCategories - the Recycle Bin only enters through
        // the explicit picker opt-in.
        var settings = new AppSettings();

        var selected = WindowsCleanupService.Catalog
            .Where(c => QuickCleanService.IsWindowsSelected(c, settings))
            .ToList();

        Assert.All(selected, c => Assert.Equal(CleanupRisk.Safe, c.Risk));
    }

    // ---- Activity recording: a run that removed nothing must not be silent ----

    /// <summary>Regression: Registry Care Quick Clean that examined candidates but
    /// removed none used to write no activity entry at all, so it was
    /// indistinguishable from a Quick Clean that never ran.</summary>
    [Fact]
    public void ANoOpRunThatExaminedCandidatesIsStillRecorded()
    {
        var result = new QuickCleanResult(0, 0, ["Software\\Foo: Key not found (already clean)"],
            Examined: 42);

        Assert.True(QuickCleanService.ShouldRecordActivity(result));
    }

    [Fact]
    public void ARunWithNothingConfiguredRecordsNothing()
    {
        // The user selected no categories at all: a no-op, not an event.
        var result = new QuickCleanResult(0, 0, [], "No categories selected.");

        Assert.False(QuickCleanService.ShouldRecordActivity(result));
    }

    [Fact]
    public void ACleaningRunIsNotTreatedAsANoOp()
    {
        // The cleaning branch in RunAsync handles these; ShouldRecordActivity must
        // not also fire or the run would log itself twice.
        Assert.False(QuickCleanService.ShouldRecordActivity(new QuickCleanResult(7, 0, [])));
        Assert.False(QuickCleanService.ShouldRecordActivity(new QuickCleanResult(0, 1024, [])));
    }

    [Fact]
    public void TheNoOpEntryNamesTheCandidateCountSkipsAndReason()
    {
        var result = new QuickCleanResult(0, 0, ["a", "b", "c"],
            "Registry cleanup was refused: no restore point.", Examined: 12);

        var detail = QuickCleanService.NothingRemovedDetail(result);

        Assert.Equal("Nothing removed from 12 candidate(s) - 3 skipped - Registry cleanup was refused: no restore point.", detail);
    }

    [Fact]
    public void TheNoOpEntryOmitsPartsThatAreAbsent()
    {
        var detail = QuickCleanService.NothingRemovedDetail(
            new QuickCleanResult(0, 0, [], Examined: 3));

        Assert.Equal("Nothing removed from 3 candidate(s)", detail);
    }

    [Fact]
    public void TheOverviewCardAndTheActivityEntryUseTheSameWording()
    {
        // The card must not say "0 item(s) removed" while the log explains the run.
        var result = new QuickCleanResult(0, 0, ["x", "y"], Examined: 9);

        Assert.Equal(QuickCleanService.NothingRemovedDetail(result) + ".", QuickCleanService.SummaryText(result));
        Assert.Equal("Nothing removed from 9 candidate(s) - 2 skipped.", QuickCleanService.SummaryText(result));
    }

    [Fact]
    public void TheOverviewCardPrefersTheAreasOwnReason()
    {
        // A reason from the area itself beats the generic no-op sentence, because
        // "No browsers selected." tells the user what to change and the generic
        // one does not.
        Assert.Equal("No browsers selected.",
            QuickCleanService.SummaryText(new QuickCleanResult(0, 0, [], "No browsers selected.")));
        Assert.Equal("No registry values were removed.",
            QuickCleanService.SummaryText(new QuickCleanResult(0, 0, [], "No registry values were removed.", Examined: 4)));
    }

    [Fact]
    public void TheOverviewCardStillSummarizesASuccessfulClean()
    {
        Assert.Equal("5 item(s) removed, 1.0 KB recovered - 2 skipped.",
            QuickCleanService.SummaryText(new QuickCleanResult(5, 1024, ["a", "b"])));
    }

    [Fact]
    public void RegistryCleanBreakdownIsGroupedByCategoryLargestFirst()
    {
        var cleaned = new[]
        {
            new RegistryFinding("HKCU", @"Control Panel\Desktop\MuiCached\a", "x", true, 90, "MUI Cache"),
            new RegistryFinding("HKCU", @"Control Panel\Desktop\MuiCached\b", "x", true, 90, "MUI Cache"),
            new RegistryFinding("HKCU", @"AppEvents\Schemes\Apps\z", "x", true, 90, "Sound AppEvents")
        };

        var lines = QuickCleanService.RegistryDetailLines(cleaned);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal("MUI Cache - 2 item(s)", lines[0]);
        Assert.Equal("Sound AppEvents - 1 item(s)", lines[1]);
    }

    [Fact]
    public void RegistryCleanBreakdownIsAbsentWhenNothingWasCleaned()
    {
        // Keeps the Activity card un-expandable rather than showing an empty list.
        Assert.Null(QuickCleanService.RegistryDetailLines([]));
        Assert.Null(QuickCleanService.RegistryDetailLines(null));
    }

    [Fact]
    public void ExaminedDefaultsToZeroSoTheRecordStaysSourceCompatible()
    {
        // Every pre-existing construction site passes five arguments; the new
        // candidate count must not be required of any of them.
        var legacy = new QuickCleanResult(3, 100, [], null, null);

        Assert.Equal(0, legacy.Examined);
        Assert.False(QuickCleanService.ShouldRecordActivity(legacy));
    }
}
