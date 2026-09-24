using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Covers the Quick Clean selection rules: which Windows categories are
/// eligible, and how the Review-risk Recycle Bin is opted in (explicit tick in
/// the Quick Clean picker only). These tests are read-only - they exercise the
/// selection logic, never an actual Quick Clean run.</summary>
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
}
