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
        var chrome = _settings.FindBrowserMonitor("chrome");
        var edge = _settings.FindBrowserMonitor("edge");
        var firefox = _settings.FindBrowserMonitor("firefox");
        ChromeEnabled.IsChecked = chrome?.Enabled ?? true;
        ChromeAction.SelectedIndex = ToComboIndex(chrome?.AfterExit ?? ExitAction.CleanAndNotify);
        EdgeEnabled.IsChecked = edge?.Enabled ?? true;
        EdgeAction.SelectedIndex = ToComboIndex(edge?.AfterExit ?? ExitAction.CleanAndNotify);
        FirefoxEnabled.IsChecked = firefox?.Enabled ?? true;
        FirefoxAction.SelectedIndex = ToComboIndex(firefox?.AfterExit ?? ExitAction.CleanAndNotify);
        UpdateExitItemsButton(ChromeItemsButton, "chrome");
        UpdateExitItemsButton(EdgeItemsButton, "edge");
        UpdateExitItemsButton(FirefoxItemsButton, "firefox");
        UpdateMonitorHint();

        // Low disk space
        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        _unitIsMb = string.Equals(_settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
        FreeSpaceUnit.SelectedIndex = _unitIsMb ? 1 : 0;
        ApplyFreeSpaceBounds(_unitIsMb);
        var loaded = RoundForUnit(_unitIsMb ? _settings.SystemMonitorFreeSpaceGb * MbPerGb : _settings.SystemMonitorFreeSpaceGb, _unitIsMb);
        FreeSpaceBox.Value = Math.Clamp(loaded, FreeSpaceBox.Minimum, FreeSpaceBox.Maximum);
        SystemMonitorAction.SelectedIndex = ToComboIndex(_settings.SystemMonitorAction);
        UpdateMonitorItemsSummary();

        // Startup / idle / recycle bin
        StartupCleanToggle.IsChecked = _settings.CleanAtStartup;
        IdleCleanToggle.IsChecked = _settings.IdleCleanEnabled;
        IdleMinutesBox.Value = Math.Clamp(_settings.IdleCleanMinutes, 1, 240);
        RecycleBinToggle.IsChecked = _settings.RecycleBinAutoEmptyEnabled;
        RecycleBinDaysBox.Value = Math.Clamp(_settings.RecycleBinAutoEmptyDays, 1, 365);

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

    private async void BrowserMonitor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyMonitor("chrome", ChromeEnabled, ChromeAction);
        ApplyMonitor("edge", EdgeEnabled, EdgeAction);
        ApplyMonitor("firefox", FirefoxEnabled, FirefoxAction);
        UpdateMonitorHint();
        await SaveAndApplyAsync();
    }

    private void ApplyMonitor(string id, CheckBox enabled, ComboBox action)
    {
        var monitor = _settings.FindBrowserMonitor(id);
        if (monitor is null)
        {
            monitor = new BrowserMonitorSetting { Browser = id };
            _settings.BrowserMonitors.Add(monitor);
        }
        monitor.Enabled = enabled.IsChecked == true;
        monitor.AfterExit = FromComboIndex(action.SelectedIndex);
    }

    private void UpdateExitItemsButton(Button button, string browser)
    {
        var monitor = _settings.FindBrowserMonitor(browser);
        button.Content = monitor?.Items is { } chosen ? $"{chosen.Count} item(s)…" : "Safe items (default)…";
    }

    private void UpdateMonitorHint()
        => MonitoringHint.Text = _settings.CleanOnBrowserExit
            ? "Cleans the items chosen per browser (safe items by default). The browser that just closed is cleaned even if other browsers are still open; passwords, cookies, history, and other destructive items are never cleaned automatically unless you opt them in. CleanMachine runs in the background and starts with Windows so exits are detected."
            : "Monitoring is off - browsers are only cleaned when you run it manually on the Browser Cleaner page.";

    private async void ChooseExitItems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string browser }) return;
        var monitor = _settings.FindBrowserMonitor(browser);
        var browserDefinition = BrowserCatalog.Find(browser);
        if (monitor is null || browserDefinition is null) return;

        var effective = _settings.EffectiveExitItems(browser);
        var panel = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Id, CheckBox Box)>();
        var hasDestructive = BrowserCatalog.ItemsFor(browserDefinition.Family).Any(i => i.Destructive);
        if (hasDestructive)
            panel.Children.Add(new TextBlock
            {
                Text = "Ticking a destructive item (cookies, history, passwords) lets the automatic close-clean remove it without asking. These stay off by default.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E)),
                Margin = new Thickness(0, 0, 0, 4)
            });
        foreach (var item in BrowserCatalog.ItemsFor(browserDefinition.Family))
        {
            var box = new CheckBox
            {
                Content = item.Destructive ? $"{item.Name}  (destructive)" : item.Name,
                IsChecked = effective.Contains(item.Id),
                MinHeight = 26
            };
            if (item.Destructive)
            {
                box.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E));
                ToolTipService.SetToolTip(box, "Destructive: when ticked, this is removed automatically every time the browser closes.");
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

    private List<(string Key, string Label, bool Checked)> MonitorItems() =>
        WindowsCleanupService.Catalog
            .Where(c => c.Risk == CleanupRisk.Safe)
            .Select(c => (c.Id, c.Name, _settings.SystemMonitorCategories is { } set
                ? set.Contains(c.Id)
                : WindowsCleanupService.IsEnabled(c, _settings)))
            .ToList();

    private void UpdateMonitorItemsSummary()
    {
        if (_settings.SystemMonitorCategories is { } set)
        {
            var total = WindowsCleanupService.Catalog.Count(c => c.Risk == CleanupRisk.Safe);
            var chosen = MonitorItems().Count(i => i.Checked);
            MonitorItemsSummary.Text = chosen == 0
                ? "No items selected - nothing will be cleaned."
                : $"{chosen} of {total} safe categories selected.";
        }
        else
        {
            MonitorItemsSummary.Text = "All enabled safe categories (default).";
        }
    }

    private async void ChooseMonitorItems_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (var (key, label, chk) in MonitorItems())
        {
            var box = new CheckBox { Content = label, IsChecked = chk };
            boxes.Add((key, box));
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = "Safe categories to clean (low-disk, startup, and idle cleans)",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _settings.SystemMonitorCategories = boxes
            .Where(b => b.Box.IsChecked == true)
            .Select(b => b.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();
        UpdateMonitorItemsSummary();
        StatusText.Text = "Categories saved.";
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
        _settings.IdleCleanEnabled = IdleCleanToggle.IsChecked == true;
        _settings.IdleCleanMinutes = double.IsNaN(IdleMinutesBox.Value) ? 15 : (int)Math.Clamp(IdleMinutesBox.Value, 1, 240);
        _settings.RecycleBinAutoEmptyEnabled = RecycleBinToggle.IsChecked == true;
        _settings.RecycleBinAutoEmptyDays = double.IsNaN(RecycleBinDaysBox.Value) ? 30 : (int)Math.Clamp(RecycleBinDaysBox.Value, 1, 365);

        await _settings.SaveAsync();
        (App.Current as App)?.ApplyBackgroundServices(_settings);
        StatusText.Text = "Saved.";
    }
}
