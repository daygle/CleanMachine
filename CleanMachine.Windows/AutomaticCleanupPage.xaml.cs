using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;

namespace CleanMachine.Windows;

/// <summary>All of CleanMachine's hands-off cleaning lives here: clean when a browser
/// closes, when free disk space runs low, at startup, while the PC is idle, and by
/// keeping the Recycle Bin tidy. Every control saves instantly and re-applies the
/// background services, so there is no Save button.</summary>
public sealed partial class AutomaticCleanupPage : Page
{
    private AppSettings _settings = new();
    private const double MbPerGb = 1024.0;
    // Suppresses instant-save while settings are being loaded into the controls.
    private bool _ready;
    private bool _unitIsMb;

    public AutomaticCleanupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _ready = false;
        _settings = await AppSettings.LoadAsync();

        // Browser monitoring
        CleanToggle.IsChecked = _settings.CleanOnBrowserExit;
        UpdateMonitorHint();
        await BuildBrowserRowsAsync();

        // Low disk space
        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        _unitIsMb = string.Equals(_settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
        FreeSpaceUnit.SelectedIndex = _unitIsMb ? 1 : 0;
        ApplyFreeSpaceBounds(_unitIsMb);
        var loaded = RoundForUnit(_unitIsMb ? _settings.SystemMonitorFreeSpaceGb * MbPerGb : _settings.SystemMonitorFreeSpaceGb, _unitIsMb);
        FreeSpaceBox.Value = Math.Clamp(loaded, FreeSpaceBox.Minimum, FreeSpaceBox.Maximum);
        SystemMonitorAction.SelectedIndex = ToComboIndex(_settings.SystemMonitorAction);

        // Startup / idle / recycle bin
        StartupCleanToggle.IsChecked = _settings.CleanAtStartup;
        StartupNotifyToggle.IsChecked = _settings.StartupCleanNotify;
        IdleCleanToggle.IsChecked = _settings.IdleCleanEnabled;
        IdleMinutesBox.Value = Math.Clamp(_settings.IdleCleanMinutes, 1, 240);
        IdleNotifyToggle.IsChecked = _settings.IdleCleanNotify;
        RecycleBinToggle.IsChecked = _settings.RecycleBinAutoEmptyEnabled;
        RecycleBinDaysBox.Value = Math.Clamp(_settings.RecycleBinAutoEmptyDays, 1, 365);
        RecycleBinNotifyToggle.IsChecked = _settings.RecycleBinAutoEmptyNotify;

        UpdateItemsSummary(MonitorItemsSummary, MonitorItems);
        UpdateItemsSummary(StartupItemsSummary, StartupItems);
        UpdateItemsSummary(IdleItemsSummary, IdleItems);

        _ready = true;
    }

    // ---- Browser monitoring ------------------------------------------------

    private async void CleanToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true;
        UpdateMonitorHint();
        await SaveAndApplyAsync();
    }

    // One row per browser in the table: the id plus its three controls.
    private readonly List<(string Id, CheckBox Enabled, ComboBox Action, Button Items)> _monitorRows = [];

    /// <summary>Adds a row for every installed browser Browser Cleaner supports.
    /// Uninstalled ones keep their saved settings so they take effect if the
    /// browser is installed later. A browser with no saved monitor (one this page
    /// did not list before) starts unticked - exactly what the background agent
    /// does for it - so nothing starts cleaning on exit until the user opts in.</summary>
    private async Task BuildBrowserRowsAsync()
    {
        var installed = await Task.Run(() => BrowserCatalog.InstalledCacheBrowsers());

        foreach (var (_, enabled, action, items) in _monitorRows)
        {
            BrowserGrid.Children.Remove(enabled);
            BrowserGrid.Children.Remove(action);
            BrowserGrid.Children.Remove(items);
        }
        _monitorRows.Clear();
        while (BrowserGrid.RowDefinitions.Count > 1)
            BrowserGrid.RowDefinitions.RemoveAt(BrowserGrid.RowDefinitions.Count - 1);

        foreach (var browser in installed)
        {
            var monitor = _settings.FindBrowserMonitor(browser.Id);
            var row = BrowserGrid.RowDefinitions.Count;
            BrowserGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var enabled = new CheckBox { Content = browser.Name, IsChecked = monitor?.Enabled ?? false, Margin = new Thickness(0, 4, 0, 4) };
            var action = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
            action.Items.Add(new ComboBoxItem { Content = "Do nothing" });
            action.Items.Add(new ComboBoxItem { Content = "Clean silently" });
            action.Items.Add(new ComboBoxItem { Content = "Clean and notify" });
            action.SelectedIndex = ToComboIndex(monitor?.AfterExit ?? ExitAction.CleanAndNotify);
            var items = new Button
            {
                Tag = browser.Id,
                Padding = new Thickness(14, 8, 14, 8),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            UpdateExitItemsButton(items, browser.Id);

            enabled.Checked += (_, _) => OnMonitorRowChanged();
            enabled.Unchecked += (_, _) => OnMonitorRowChanged();
            action.SelectionChanged += (_, _) => OnMonitorRowChanged();
            items.Click += ChooseExitItems_Click;

            Grid.SetRow(enabled, row); Grid.SetColumn(enabled, 0);
            Grid.SetRow(action, row); Grid.SetColumn(action, 1);
            Grid.SetRow(items, row); Grid.SetColumn(items, 2);
            BrowserGrid.Children.Add(enabled);
            BrowserGrid.Children.Add(action);
            BrowserGrid.Children.Add(items);
            _monitorRows.Add((browser.Id, enabled, action, items));
        }

        BrowserTable.Visibility = installed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoBrowsersNote.Visibility = installed.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnMonitorRowChanged()
    {
        if (!_ready) return;
        foreach (var (id, enabled, action, _) in _monitorRows)
            ApplyMonitor(id, enabled, action);
        UpdateMonitorHint();
        await SaveAndApplyAsync();
    }

    private void ApplyMonitor(string id, CheckBox enabled, ComboBox action)
    {
        var monitor = _settings.FindBrowserMonitor(id);
        if (monitor is null)
        {
            // Not saved yet (a browser this page did not list before): only start
            // tracking it once the user actually turns it on.
            if (enabled.IsChecked != true) return;
            monitor = new BrowserMonitorSetting { Browser = id };
            _settings.BrowserMonitors.Add(monitor);
        }
        monitor.Enabled = enabled.IsChecked == true;
        monitor.AfterExit = FromComboIndex(action.SelectedIndex);
    }

    private void UpdateExitItemsButton(Button button, string browser)
    {
        // Match the "Choose items..." buttons in the other sections; the current
        // selection shows on hover instead of in the label.
        var monitor = _settings.FindBrowserMonitor(browser);
        button.Content = "Choose items...";
        ToolTipService.SetToolTip(button, monitor?.Items is { } chosen
            ? $"{chosen.Count} item(s) selected"
            : "Caches (default)");
    }

    private void UpdateMonitorHint()
        => MonitoringHint.Text = _settings.CleanOnBrowserExit
            ? "Cleans the items chosen per browser (caches by default). The browser that just closed is cleaned even if other browsers are still open; passwords, cookies, history and other saved data are never cleaned automatically unless you opt them in. CleanMachine runs in the background and starts with Windows so exits are detected."
            : "Monitoring is off - browsers are only cleaned when you run it manually on the Browser Cleaner page.";

    private async void ChooseExitItems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string browser }) return;
        var browserDefinition = BrowserCatalog.Find(browser);
        if (browserDefinition is null) return;
        // A browser with no saved monitor yet can still have its items chosen; the
        // monitor is created switched off, so choosing items alone cleans nothing.
        var monitor = _settings.FindBrowserMonitor(browser);
        if (monitor is null)
        {
            monitor = new BrowserMonitorSetting { Browser = browser, Enabled = false };
            _settings.BrowserMonitors.Add(monitor);
        }

        var effective = _settings.EffectiveExitItems(browser);
        var panel = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Id, CheckBox Box)>();
        var hasDestructive = BrowserCatalog.ItemsFor(browserDefinition.Family).Any(i => i.Destructive);
        if (hasDestructive)
            panel.Children.Add(new TextBlock
            {
                Text = "Ticking a Saved Data item (cookies, history, passwords) lets the automatic close-clean remove it without asking. These stay off by default.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E)),
                Margin = new Thickness(0, 0, 0, 4)
            });
        foreach (var item in BrowserCatalog.ItemsFor(browserDefinition.Family))
        {
            var box = new CheckBox
            {
                Content = item.Destructive ? $"{item.Name}  (Saved Data)" : item.Name,
                IsChecked = effective.Contains(item.Id),
                MinHeight = 26
            };
            if (item.Destructive)
            {
                box.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E));
                ToolTipService.SetToolTip(box, "Saved Data: when ticked, this is removed automatically every time the browser closes.");
            }
            boxes.Add((item.Id, box));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = $"{browserDefinition.Name} - items cleaned when it closes",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        monitor.Items = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdateExitItemsButton((Button)sender, browser);
        await SaveAndApplyAsync();
    }

    private static int ToComboIndex(ExitAction action) => action switch
    {
        ExitAction.DoNothing => 0,
        ExitAction.CleanSilently => 1,
        _ => 2
    };

    private static ExitAction FromComboIndex(int index) => index switch
    {
        0 => ExitAction.DoNothing,
        1 => ExitAction.CleanSilently,
        _ => ExitAction.CleanAndNotify
    };

    // ---- Low disk space ----------------------------------------------------

    private void FreeSpaceUnit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var newIsMb = FreeSpaceUnit.SelectedIndex == 1;
        if (newIsMb == _unitIsMb) return;
        var current = double.IsNaN(FreeSpaceBox.Value) ? 0 : FreeSpaceBox.Value;
        ApplyFreeSpaceBounds(newIsMb);
        var converted = RoundForUnit(newIsMb ? current * MbPerGb : current / MbPerGb, newIsMb);
        FreeSpaceBox.Value = Math.Clamp(converted, FreeSpaceBox.Minimum, FreeSpaceBox.Maximum);
        _unitIsMb = newIsMb;
        _ = SaveAndApplyAsync();
    }

    private void ApplyFreeSpaceBounds(bool isMb)
    {
        if (isMb)
        {
            FreeSpaceBox.Minimum = 50; FreeSpaceBox.Maximum = 102400;
            FreeSpaceBox.SmallChange = 50; FreeSpaceBox.LargeChange = 500;
            FreeSpaceBox.NumberFormatter = MakeFormatter(0);
        }
        else
        {
            FreeSpaceBox.Minimum = 0.1; FreeSpaceBox.Maximum = 100;
            FreeSpaceBox.SmallChange = 0.1; FreeSpaceBox.LargeChange = 1;
            FreeSpaceBox.NumberFormatter = MakeFormatter(2);
        }
    }

    private static DecimalFormatter MakeFormatter(int fractionDigits) => new()
    {
        IntegerDigits = 1,
        FractionDigits = fractionDigits,
        IsGrouped = false
    };

    private static double RoundForUnit(double value, bool isMb) => isMb ? Math.Round(value) : Math.Round(value, 2);

    // ---- "Choose items..." picker ------------------------------------------

    /// <summary>What one trigger cleans: Windows Cleanup category ids (null = every
    /// Safe category enabled on the Windows Cleanup page), plus the browsers whose
    /// Internet Cache and the applications whose temp files it also clears.</summary>
    private sealed record TriggerItems(HashSet<string>? Categories, HashSet<string> Browsers, HashSet<string> Apps);

    private static readonly SolidColorBrush HeadingBrush = new(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30));
    private static readonly SolidColorBrush GroupBrush = new(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89));
    private static readonly SolidColorBrush MutedBrush = new(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F));
    private static readonly SolidColorBrush CardBorderBrush = new(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7));

    // Only Safe-risk Windows categories are offered: automatic runs never touch
    // Review/Advanced categories.
    private static IEnumerable<CleanupCategory> SafeCategories =>
        WindowsCleanupService.Catalog.Where(c => c.Risk == CleanupRisk.Safe);

    private bool IsCategoryChecked(CleanupCategory category, HashSet<string>? selection)
        => selection is { } set ? set.Contains(category.Id) : WindowsCleanupService.IsEnabled(category, _settings);

    private void UpdateItemsSummary(TextBlock target, TriggerItems items)
    {
        string windows;
        if (items.Categories is { } set)
        {
            var total = SafeCategories.Count();
            var chosen = SafeCategories.Count(c => set.Contains(c.Id));
            windows = $"Windows Cleanup: {chosen} of {total}";
        }
        else
        {
            windows = "Windows Cleanup: everything enabled on its page";
        }
        var browserNames = items.Browsers
            .Select(BrowserCatalog.Find).OfType<BrowserDefinition>()
            .Select(b => b.Name).ToList();
        var browsers = browserNames.Count == 0 ? "Browser caches: none" : $"Browser caches: {string.Join(", ", browserNames)}";
        var appCount = items.Apps.Count(id => AppCatalog.Find(id) is not null);
        var apps = appCount == 0 ? "Application temp files: none" : $"Application temp files: {appCount} app(s)";

        target.Text = items.Categories is { Count: 0 } && browserNames.Count == 0 && appCount == 0
            ? "Nothing selected - this will clean nothing."
            : $"{windows}  -  {browsers}  -  {apps}";
    }

    /// <summary>Shows the item picker for one trigger, grouped by the page each item
    /// comes from (Windows Cleanup, Browser Cleaner, Application Cleanup), and
    /// returns the chosen items, or null if the dialog was cancelled. Each page
    /// heading is a tri-state checkbox that selects or clears its whole section.
    /// Browsers and apps that are selected but no longer installed keep their tick,
    /// so reinstalling one brings the choice back.</summary>
    private async Task<TriggerItems?> PickItemsAsync(string title, TriggerItems current)
    {
        var browsers = await Task.Run(() => BrowserCatalog.InstalledCacheBrowsers());
        var apps = await Task.Run(() => AppCleanupService.InstalledApps());

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Note("Tick what this trigger should clean. Items are grouped by the CleanMachine page they come from; tick a page heading to select or clear its whole section. Only items that are safe to remove unattended are listed."));

        // Windows Cleanup, sub-grouped the same way as its own page.
        var categoryBoxes = new List<(string Id, CheckBox Box)>();
        var windowsSection = AddSection(panel, "Windows Cleanup",
            "Safe categories only. Review and Advanced categories (Recycle Bin, Downloads, Prefetch, ...) are never cleaned automatically.");
        foreach (var group in SafeCategories.GroupBy(c => c.Group))
        {
            windowsSection.Children.Children.Add(GroupLabel(group.Key));
            foreach (var category in group)
            {
                var box = ChildBox(category.Name, category.Description, IsCategoryChecked(category, current.Categories));
                categoryBoxes.Add((category.Id, box));
                windowsSection.Children.Children.Add(box);
            }
        }
        WireSection(windowsSection.Header, categoryBoxes.Select(b => b.Box).ToList());

        var browserBoxes = new List<(string Id, CheckBox Box)>();
        var appBoxes = new List<(string Id, CheckBox Box)>();
        var browserSection = AddSection(panel, "Browser Cleaner - Internet Cache",
            "Clears each ticked browser's cache. History, cookies, passwords and other saved data are never touched, and the browser can stay open (files it is using are skipped).");
        foreach (var browser in browsers)
        {
            var box = ChildBox(browser.Name, "Internet Cache", current.Browsers.Contains(browser.Id));
            browserBoxes.Add((browser.Id, box));
            browserSection.Children.Children.Add(box);
        }
        if (browsers.Count == 0)
            browserSection.Children.Children.Add(Note("No supported browsers were found on this PC."));
        WireSection(browserSection.Header, browserBoxes.Select(b => b.Box).ToList());

        var appSection = AddSection(panel, "Application Cleanup - Temp Files",
            "Clears the temporary, cache and log files listed for each ticked app on the Application Cleanup page.");
        foreach (var group in apps.GroupBy(a => a.Group))
        {
            appSection.Children.Children.Add(GroupLabel(group.Key));
            foreach (var app in group)
            {
                var box = ChildBox(app.Name, null, current.Apps.Contains(app.Id));
                appBoxes.Add((app.Id, box));
                appSection.Children.Children.Add(box);
            }
        }
        if (apps.Count == 0)
            appSection.Children.Children.Add(Note("No supported applications were found on this PC."));
        WireSection(appSection.Header, appBoxes.Select(b => b.Box).ToList());

        panel.Children.Add(Note("Registry Care is not available here: registry fixes need a review and a backup each time, so they only run from the Registry Care page or a schedule."));

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460,
                Padding = new Thickness(0, 0, 12, 0)
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        static HashSet<string> Ticked(IEnumerable<(string Id, CheckBox Box)> boxes)
            => boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Keep ticks for browsers/apps the dialog could not show (not installed now).
        var chosenBrowsers = Ticked(browserBoxes);
        chosenBrowsers.UnionWith(current.Browsers.Where(id => browserBoxes.All(b => !b.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));
        var chosenApps = Ticked(appBoxes);
        chosenApps.UnionWith(current.Apps.Where(id => appBoxes.All(b => !b.Id.Equals(id, StringComparison.OrdinalIgnoreCase))));

        return new TriggerItems(Ticked(categoryBoxes), chosenBrowsers, chosenApps);
    }

    /// <summary>Adds one page's card: a bold tri-state heading checkbox, a one-line
    /// explanation, and an indented panel for its items.</summary>
    private static (CheckBox Header, StackPanel Children) AddSection(StackPanel parent, string heading, string explanation)
    {
        var header = new CheckBox
        {
            Content = new TextBlock
            {
                Text = heading,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = HeadingBrush
            }
        };
        var children = new StackPanel { Spacing = 2, Margin = new Thickness(28, 4, 0, 0) };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = explanation,
            FontSize = 11,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0)
        });
        body.Children.Add(children);
        parent.Children.Add(new Border
        {
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = body
        });
        return (header, children);
    }

    private static TextBlock GroupLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        Foreground = GroupBrush,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = MutedBrush,
        TextWrapping = TextWrapping.Wrap
    };

    private static CheckBox ChildBox(string label, string? tooltip, bool isChecked)
    {
        var box = new CheckBox { Content = label, IsChecked = isChecked, MinHeight = 28 };
        if (!string.IsNullOrWhiteSpace(tooltip))
            ToolTipService.SetToolTip(box, tooltip);
        return box;
    }

    /// <summary>Keeps a section heading in step with its items: checked when all are,
    /// cleared when none are, indeterminate in between; clicking the heading ticks or
    /// clears every item.</summary>
    private static void WireSection(CheckBox header, List<CheckBox> children)
    {
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            var ticked = children.Count(c => c.IsChecked == true);
            header.IsChecked = ticked == 0 ? false : ticked == children.Count ? true : null;
            syncing = false;
        }
        header.IsEnabled = children.Count > 0;
        header.Click += (_, _) =>
        {
            var value = header.IsChecked == true;
            syncing = true;
            foreach (var child in children) child.IsChecked = value;
            syncing = false;
            Refresh();
        };
        foreach (var child in children)
        {
            child.Checked += (_, _) => { if (!syncing) Refresh(); };
            child.Unchecked += (_, _) => { if (!syncing) Refresh(); };
        }
        Refresh();
    }

    private TriggerItems MonitorItems => new(_settings.SystemMonitorCategories, _settings.SystemMonitorBrowsers, _settings.SystemMonitorApps);
    private TriggerItems StartupItems => new(_settings.StartupCleanCategories, _settings.StartupCleanBrowsers, _settings.StartupCleanApps);
    private TriggerItems IdleItems => new(_settings.IdleCleanCategories, _settings.IdleCleanBrowsers, _settings.IdleCleanApps);

    private async void ChooseMonitorItems_Click(object sender, RoutedEventArgs e)
    {
        var chosen = await PickItemsAsync("Items to clean when disk space is low", MonitorItems);
        if (chosen is null) return;
        _settings.SystemMonitorCategories = chosen.Categories;
        _settings.SystemMonitorBrowsers = chosen.Browsers;
        _settings.SystemMonitorApps = chosen.Apps;
        await _settings.SaveAsync();
        UpdateItemsSummary(MonitorItemsSummary, MonitorItems);
        StatusText.Text = "Saved.";
    }

    private async void ChooseStartupItems_Click(object sender, RoutedEventArgs e)
    {
        var chosen = await PickItemsAsync("Items to clean at startup", StartupItems);
        if (chosen is null) return;
        _settings.StartupCleanCategories = chosen.Categories;
        _settings.StartupCleanBrowsers = chosen.Browsers;
        _settings.StartupCleanApps = chosen.Apps;
        await _settings.SaveAsync();
        UpdateItemsSummary(StartupItemsSummary, StartupItems);
        StatusText.Text = "Saved.";
    }

    private async void ChooseIdleItems_Click(object sender, RoutedEventArgs e)
    {
        var chosen = await PickItemsAsync("Items to clean when idle", IdleItems);
        if (chosen is null) return;
        _settings.IdleCleanCategories = chosen.Categories;
        _settings.IdleCleanBrowsers = chosen.Browsers;
        _settings.IdleCleanApps = chosen.Apps;
        await _settings.SaveAsync();
        UpdateItemsSummary(IdleItemsSummary, IdleItems);
        StatusText.Text = "Saved.";
    }

    // ---- Instant-save handlers ---------------------------------------------

    private void Setting_Changed(object sender, RoutedEventArgs e) { if (_ready) _ = SaveAndApplyAsync(); }
    private void Setting_ComboChanged(object sender, SelectionChangedEventArgs e) { if (_ready) _ = SaveAndApplyAsync(); }
    private void FreeSpace_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) { if (_ready) _ = SaveAndApplyAsync(); }
    private void IdleMinutes_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) { if (_ready) _ = SaveAndApplyAsync(); }
    private void RecycleBinDays_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) { if (_ready) _ = SaveAndApplyAsync(); }

    /// <summary>Reads every control into settings, persists, and re-applies the
    /// background services (agent + Windows startup) so they match what is enabled.</summary>
    private async Task SaveAndApplyAsync()
    {
        _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true;

        _settings.SystemMonitoringEnabled = SystemMonitoringToggle.IsChecked == true;
        var mb = FreeSpaceUnit.SelectedIndex == 1;
        var entered = double.IsNaN(FreeSpaceBox.Value) ? (mb ? MbPerGb : 1.0) : FreeSpaceBox.Value;
        _settings.SystemMonitorFreeSpaceGb = Math.Clamp(mb ? entered / MbPerGb : entered, 0.01, 100);
        _settings.SystemMonitorFreeSpaceUnit = mb ? "MB" : "GB";
        _settings.SystemMonitorAction = FromComboIndex(SystemMonitorAction.SelectedIndex);

        _settings.CleanAtStartup = StartupCleanToggle.IsChecked == true;
        _settings.StartupCleanNotify = StartupNotifyToggle.IsChecked == true;
        _settings.IdleCleanEnabled = IdleCleanToggle.IsChecked == true;
        _settings.IdleCleanMinutes = double.IsNaN(IdleMinutesBox.Value) ? 15 : (int)Math.Clamp(IdleMinutesBox.Value, 1, 240);
        _settings.IdleCleanNotify = IdleNotifyToggle.IsChecked == true;
        _settings.RecycleBinAutoEmptyEnabled = RecycleBinToggle.IsChecked == true;
        _settings.RecycleBinAutoEmptyDays = double.IsNaN(RecycleBinDaysBox.Value) ? 30 : (int)Math.Clamp(RecycleBinDaysBox.Value, 1, 365);
        _settings.RecycleBinAutoEmptyNotify = RecycleBinNotifyToggle.IsChecked == true;

        await _settings.SaveAsync();
        if (App.Current is App app) _ = app.ApplyBackgroundServices(_settings);
        StatusText.Text = "Saved.";
    }
}
