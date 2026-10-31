using CleanMachine.Windows;
using Xunit;

namespace CleanMachine.Windows.Tests;

/// <summary>Tests for the tray context menu's quick-clean bookkeeping. The real
/// menu is Win32 Win32 popup-menu code, but <see cref="TrayIcon.BuildMenu"/> is a
/// pure model of exactly what <see cref="TrayIcon.ShowContextMenu"/> draws, so these
/// tests pin the command ids, labels, ordering and the greyed-while-busy behaviour
/// without driving a real tray icon. The instance-based tests (in the "Cleaning"
/// collection, because they observe process-wide <see cref="CleaningActivity"/>
/// statics) cover the other half of the story: the icon's busy flag tracking the
/// hub's transitions, which decides what BuildMenu gets called with.</summary>
public sealed class TrayIconMenuTests
{
    private static IReadOnlyList<TrayMenuItem> Build(bool cleanRunning = false)
    {
        var menu = TrayIcon.BuildMenu(cleanRunning);
        // The model must be freshly built every time: the same call with the same
        // input is a new list, so one test cannot mutate another's view.
        Assert.NotSame(TrayIcon.BuildMenu(cleanRunning), menu);
        return menu;
    }

    private static readonly string[] AreaTitles =
        [QuickCleanService.Title(QuickCleanArea.Browsers), QuickCleanService.Title(QuickCleanArea.Windows),
         QuickCleanService.Title(QuickCleanArea.Registry), QuickCleanService.Title(QuickCleanArea.Apps)];

    [Fact]
    public void MenuDrawsOpenCleanEverythingFourQuickCleansThenExit()
    {
        var menu = Build();

        Assert.Equal(9, menu.Count); // 7 items + 2 separators
        Assert.Equal("Open CleanMachine", menu[0].Label);
        Assert.True(menu[1].IsSeparator);
        Assert.Equal("Clean Everything", menu[2].Label);
        for (var i = 0; i < AreaTitles.Length; i++)
        {
            // Reusing QuickCleanService.Title keeps the menu's wording coupled to
            // the service's: if a title ever changes, this fails with the old text.
            Assert.Equal($"Quick Clean - {AreaTitles[i]}", menu[3 + i].Label);
        }
        Assert.True(menu[7].IsSeparator);
        Assert.Equal("Exit CleanMachine", menu[8].Label);

        // Exactly the two separators, nothing between them and an accidentally
        // appended item.
        Assert.Equal([1, 7], menu.Select((item, index) => (item, index))
            .Where(pair => pair.item.IsSeparator)
            .Select(pair => pair.index).ToArray());
    }

    [Fact]
    public void QuickCleanCommandIdsAreConsecutiveAndMatchTheirAreas()
    {
        var menu = Build();

        var cleanItems = menu.Where(item => !item.IsSeparator).ToList();
        Assert.Equal(7, cleanItems.Count); // Open, Clean Everything, 4 areas, Exit

        // The four per-area ids are the consecutive block 3..6 (from TrayIcon's
        // ID_QUICKCLEAN_FIRST) and come back mapped to their areas one to one.
        var ids = cleanItems.Select(item => item.CommandId).ToArray();
        var quickCleanIds = ids.Where(id => TrayIcon.QuickCleanAreaFromCommand(id) is not null).ToArray();
        Assert.Equal([3, 4, 5, 6], quickCleanIds.Select(id => (int)id).ToArray());
        Assert.Equal(
            new[] { QuickCleanArea.Browsers, QuickCleanArea.Windows, QuickCleanArea.Registry, QuickCleanArea.Apps },
            quickCleanIds.Select(id => TrayIcon.QuickCleanAreaFromCommand(id)!.Value).ToArray());

        // Every id on the menu is unique: a collision would run the wrong clean.
        Assert.Equal(cleanItems.Count, ids.Distinct().Count());

        // "Clean Everything" sits outside the quick-clean id range: its dispatch
        // must never accidentally fall through to one of the area commands.
        var cleanEverything = cleanItems.First(item => item.Label == "Clean Everything");
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(cleanEverything.CommandId));
    }

    [Fact]
    public void QuickCleanCommandRoundTrips()
    {
        // QuickCleanCommand builds the id the menu carries for an area, and
        // QuickCleanAreaFromCommand maps it back - the pair the dispatch relies on.
        Assert.Equal(3u, TrayIcon.QuickCleanCommand(QuickCleanArea.Browsers));
        Assert.Equal(4u, TrayIcon.QuickCleanCommand(QuickCleanArea.Windows));
        Assert.Equal(5u, TrayIcon.QuickCleanCommand(QuickCleanArea.Registry));
        Assert.Equal(6u, TrayIcon.QuickCleanCommand(QuickCleanArea.Apps));
    }

    [Fact]
    public void QuickCleanAreaFromCommandRejectsEverythingElse()
    {
        var menu = Build();
        var commandIds = new HashSet<uint>(menu.Select(item => item.CommandId));

        // Non-clean commands must not be accepted by the mapper, or clicking one
        // would silently run a quick clean.
        foreach (var id in commandIds)
            Assert.Equal(id is >= 3 and <= 6, TrayIcon.QuickCleanAreaFromCommand(id) is not null);
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(0));
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(7));
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(uint.MaxValue));

        // And the mapping must never reach past the block: the range check has to
        // reject the neighbouring ids a hidden or ghost item could carry.
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(2));
        Assert.Null(TrayIcon.QuickCleanAreaFromCommand(7));
    }

    [Fact]
    public void QuickCleanItemsAreGreyedExactlyWhileACleanRuns()
    {
        var idle = Build(cleanRunning: false);
        var busy = Build(cleanRunning: true);

        void AssertStates(IReadOnlyList<TrayMenuItem> menu, bool expectCleanEnabled, string label)
        {
            foreach (var item in menu)
            {
                if (item.IsSeparator) continue;
                var isCleanItem = item.Label == "Clean Everything"
                    || item.Label.StartsWith("Quick Clean - ", StringComparison.Ordinal);
                // Open and Exit stay usable during a clean; all five clean items
                // flip together - the same _quickCleanInFlight drives both ends.
                Assert.Equal(!isCleanItem || expectCleanEnabled, item.Enabled);
            }
        }

        // The menu a user right-clicks while the tray icon is spinning: everything
        // clean is greyed. When idle: everything enabled.
        AssertStates(busy, expectCleanEnabled: false, "busy");
        AssertStates(idle, expectCleanEnabled: true, "idle");

        // And the explicit pair, items only: greyed precisely while busy, no
        // in-between (Open, Clean Everything, the four areas, Exit).
        Assert.Equal([true, false, false, false, false, false, true], busy.Where(item => !item.IsSeparator).Select(item => item.Enabled).ToArray());
        Assert.Equal(Enumerable.Repeat(true, 7).ToArray(), idle.Where(item => !item.IsSeparator).Select(item => item.Enabled).ToArray());
    }
}

/// <summary>Instance-level tests for the tray icon's greyed-while-busy flag: that
/// the constructor reads <see cref="CleaningActivity"/>'s current state and then
/// tracks its Changed events. Uses the real hub (process-wide statics), so the
/// class sits in the "Cleaning" collection alongside the other tests that touch
/// absolute <see cref="CleaningActivity.IsActive"/> asserts.</summary>
[Collection("Cleaning")]
public sealed class TrayIconInFlightStateTests
{
    [Fact]
    public void FlagTracksCleaningActivityTransitions()
    {
        Assert.False(CleaningActivity.IsActive);
        using (var tray = new TrayIcon(IntPtr.Zero, "test"))
        {
            Assert.False(tray.QuickCleanInFlightForTest);

            using (CleaningActivity.Begin())
                Assert.True(tray.QuickCleanInFlightForTest);

            // Back to zero: the flag must drop with it, so the menu re-enables.
            Assert.False(tray.QuickCleanInFlightForTest);

            using (CleaningActivity.Begin())
            {
                using (CleaningActivity.Begin())
                    Assert.True(tray.QuickCleanInFlightForTest);
                // One scope closed but another still running: stay greyed.
                Assert.True(tray.QuickCleanInFlightForTest);
            }
            Assert.False(tray.QuickCleanInFlightForTest);
        }
    }

    [Fact]
    public void ConstructorPicksUpAnAlreadyRunningClean()
    {
        // A clean started elsewhere (Overview cards, a schedule, the agent) must
        // grey the menu items from the moment the icon is created - the icon only
        // hears later transitions, so the initial state needs a direct read too.
        using (var scope = CleaningActivity.Begin())
        using (var tray = new TrayIcon(IntPtr.Zero, "test"))
        {
            Assert.True(tray.QuickCleanInFlightForTest);
            Assert.True(CleaningActivity.IsActive);
        }
        Assert.False(CleaningActivity.IsActive);
    }
}
