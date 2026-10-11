using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Pins that categories only an administrator can clean are never offered
/// to a normal (non-elevated) run, where they could only fail with "Access denied".
/// CleanMachine never asks for elevation, so these appear only when the app is
/// already running as an administrator.</summary>
public sealed class AdministratorCategoryTests
{
    private static readonly string[] AdminOnly =
    [
        "advanced-prefetch",
        "advanced-event-logs",
        "advanced-setupapi-logs",
        "advanced-delivery-optimization",
        "advanced-windows-update-cache"
    ];

    [Fact]
    public void ExactlyTheWindowsFolderCategoriesNeedAdministrator()
    {
        var needAdmin = WindowsCleanupService.Catalog
            .Where(c => c.RequiresAdministrator)
            .Select(c => c.Id)
            .Order()
            .ToArray();

        Assert.Equal(AdminOnly.Order().ToArray(), needAdmin);
    }

    [Fact]
    public void NothingUnattendedDependsOnAdministratorRights()
    {
        // Quick Clean and the automatic triggers only ever use Safe categories, so
        // none of those may need elevation.
        Assert.DoesNotContain(WindowsCleanupService.Catalog,
            c => c.Risk == CleanupRisk.Safe && c.RequiresAdministrator);
    }

    [Fact]
    public void AvailableMatchesTheProcessRights()
    {
        var available = WindowsCleanupService.Available.Select(c => c.Id).ToHashSet();

        if (WindowsCleanupService.IsElevated)
            Assert.All(AdminOnly, id => Assert.Contains(id, available));
        else
            Assert.All(AdminOnly, id => Assert.DoesNotContain(id, available));

        // Every other category is always available.
        Assert.All(WindowsCleanupService.Catalog.Where(c => !c.RequiresAdministrator),
            c => Assert.Contains(c.Id, available));
    }

    [Fact]
    public async Task ANormalRunSkipsAdministratorCategoriesInsteadOfFailingOnEachFile()
    {
        if (WindowsCleanupService.IsElevated) return; // only meaningful without elevation

        var category = WindowsCleanupService.Catalog.Single(c => c.Id == "advanced-prefetch");
        var report = await new WindowsCleanupService().CleanSelectedAsync(
            [category], new WindowsCleanupOptions(ConfirmReviewCategories: true));

        Assert.Equal(0, report.Result.ItemsRemoved);
        var issue = Assert.Single(report.Skipped);
        Assert.Contains("administrator", issue.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
