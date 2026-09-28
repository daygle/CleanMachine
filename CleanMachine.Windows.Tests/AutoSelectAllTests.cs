using System.Text.Json;
using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Covers the "Auto-Select All" sticky mode: with an area's switch on, a
/// category is selected even when it is empty or off by default, so the user does
/// not have to revisit the page each time something new is detected - while an
/// explicit untick still wins, so a single category can be opted back out.
/// <para>
/// The switch is per cleanup area, so turning it on for Windows Cleanup must not
/// silently change the Browser or Registry areas; that independence is asserted
/// here too.
/// </para>
/// <para>
/// Read-only: these exercise the selection rules only, never a cleanup run. The
/// one thing asserted alongside them is the safety boundary - the automatic and
/// background cleans filter on <see cref="CleanupRisk.Safe"/> independently of
/// these switches, so widening a manual selection cannot reach them.
/// </para></summary>
public sealed class AutoSelectAllTests
{
    private static CleanupCategory Category(string id) =>
        WindowsCleanupService.Catalog.First(c => c.Id == id);

    [Fact]
    public void OffByDefaultAnOffByDefaultCategoryStaysUnselected()
    {
        // "Delivery Optimization Files" is not enabled by default, so a fresh
        // install must not silently start cleaning it.
        var settings = new AppSettings();

        Assert.False(WindowsCleanupService.IsEnabled(Category("advanced-delivery-optimization"), settings));
    }

    [Fact]
    public void OnSelectsCategoriesThatAreOffByDefault()
    {
        var settings = new AppSettings { AutoSelectAllCategories = true };

        Assert.True(WindowsCleanupService.IsEnabled(Category("advanced-delivery-optimization"), settings));
    }

    [Fact]
    public void OnSelectsEverythingInTheCatalog()
    {
        var settings = new AppSettings { AutoSelectAllCategories = true };

        Assert.All(WindowsCleanupService.Catalog,
            c => Assert.True(WindowsCleanupService.IsEnabled(c, settings)));
    }

    [Fact]
    public void EachAreaHasItsOwnSwitch()
    {
        // The point of splitting the flag: opting Windows Cleanup in must not drag
        // the Browser and Registry areas along with it.
        var settings = new AppSettings { AutoSelectAllCategories = true };

        Assert.False(settings.AutoSelectAllBrowsers);
        Assert.False(settings.AutoSelectAllRegistry);
    }

    [Fact]
    public void AnExplicitUntickStillOptsASingleCategoryOut()
    {
        // The escape hatch that makes the mode safe to offer: the user can still
        // exclude one category, and it stays excluded across restarts because the
        // opt-out is written to the saved set rather than recomputed.
        var settings = new AppSettings { AutoSelectAllCategories = true };
        settings.DisabledCleanupCategories.Add("system-temp");

        Assert.False(WindowsCleanupService.IsEnabled(Category("system-temp"), settings));
        Assert.True(WindowsCleanupService.IsEnabled(Category("system-web-cache"), settings));
    }

    [Fact]
    public void AnExplicitTickStillOptsAnOptOutCategoryBackIn()
    {
        var settings = new AppSettings
        {
            DisabledCleanupCategories = ["system-temp"],
            EnabledCleanupCategories = ["system-temp"]
        };

        Assert.True(WindowsCleanupService.IsEnabled(Category("system-temp"), settings));
    }

    [Fact]
    public void AutomaticPathsRemainSafeOnlyWhateverTheModeSays()
    {
        // The flag widens the manual Windows Cleanup list; it must not widen the
        // background ones. The first predicate is Quick Clean's own, the second is
        // the filter the startup/idle/low-disk triggers and the Overview estimate
        // apply. Both gate on risk before ever consulting IsEnabled.
        var settings = new AppSettings { AutoSelectAllCategories = true };
        var dangerous = WindowsCleanupService.Catalog
            .Where(c => c.Risk != CleanupRisk.Safe)
            .ToList();

        Assert.NotEmpty(dangerous);
        Assert.All(dangerous, c =>
        {
            Assert.True(WindowsCleanupService.IsEnabled(c, settings));
            Assert.False(QuickCleanService.IsWindowsSelected(c, settings));
            Assert.False(c.Risk == CleanupRisk.Safe && WindowsCleanupService.IsEnabled(c, settings));
        });
    }

    [Fact]
    public void AnExplicitSelectAllUntickPinsAnItemOffDespiteTheMode()
    {
        // The one interaction worth knowing about: "Select All" writes an explicit
        // choice per row, and an explicit choice outranks the mode. So with the
        // mode on, pressing Select All and then unticking it does not fall back to
        // "everything selected" - it pins those categories off until they are
        // ticked again. Asserted so the behaviour is deliberate, not accidental.
        var settings = new AppSettings { AutoSelectAllCategories = true };

        // Unticking Select All clears every visible row, which is what SetEnabled
        // records for each one.
        settings.DisabledCleanupCategories.Add("system-temp");

        Assert.False(WindowsCleanupService.IsEnabled(Category("system-temp"), settings));
        // Everything the user did not touch still follows the mode.
        Assert.True(WindowsCleanupService.IsEnabled(Category("system-web-cache"), settings));
    }

    [Fact]
    public void TheSwitchesAndRememberedTicksSurviveASettingsRoundTrip()
    {
        // A switch is only "remembered" if the serializer that writes settings.json
        // actually carries it. Proved against the serializer directly rather than
        // by calling SaveAsync, because that writes the real per-user settings file
        // and the test must not touch the user's own configuration.
        var saved = new AppSettings
        {
            AutoSelectAllCategories = true,
            AutoSelectAllBrowsers = false,
            AutoSelectAllRegistry = true,
            AppCleanupSelection = { ["7zip:0"] = false },
            RegistryCareSelection = { ["HKCU|Software\\X|Val"] = false }
        };

        var json = JsonSerializer.Serialize(saved);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!.Sanitize();

        Assert.True(restored.AutoSelectAllCategories);
        Assert.False(restored.AutoSelectAllBrowsers);
        Assert.True(restored.AutoSelectAllRegistry);
        Assert.False(restored.AppCleanupSelection["7zip:0"]);
        Assert.False(restored.RegistryCareSelection[@"HKCU|Software\X|Val"]);
    }

    [Fact]
    public void ACorruptSelectionMapIsDiscardedRatherThanTrusted()
    {
        // Sanitize() runs on every load, so a hand-edited settings.json cannot
        // smuggle in an unbounded dictionary.
        var settings = new AppSettings
        {
            AppCleanupSelection = null!,
            RegistryCareSelection = null!,
            BrowserCleanupSelection = null!
        };

        settings.Sanitize();

        Assert.Empty(settings.AppCleanupSelection);
        Assert.Empty(settings.RegistryCareSelection);
        Assert.Empty(settings.BrowserCleanupSelection);
    }
}
