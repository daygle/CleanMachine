using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace CleanMachine.Windows;

public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();
    // Guards the unit ComboBox while settings are loaded into the controls.
    private bool _loadingUnit;
    private bool _unitIsMb;
    // Suppresses instant-save while settings are being loaded into the controls
    // (or reset by Restore Defaults), so populating them does not save. Starts
    // true so SelectionChanged events fired during InitializeComponent (from XAML
    // SelectedIndex) cannot save default values over the real settings file before
    // LoadAsync has run.
    private bool _loading = true;

    private const double MbPerGb = 1024.0;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _settings = await AppSettings.LoadAsync();

        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        _loadingUnit = true;
        _unitIsMb = string.Equals(_settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
        FreeSpaceUnit.SelectedIndex = _unitIsMb ? 1 : 0;
        ApplyFreeSpaceBounds(_unitIsMb);
        var loaded = RoundForUnit(_unitIsMb ? _settings.SystemMonitorFreeSpaceGb * MbPerGb : _settings.SystemMonitorFreeSpaceGb, _unitIsMb);
        FreeSpaceBox.Value = Math.Clamp(loaded, FreeSpaceBox.Minimum, FreeSpaceBox.Maximum);
        _loadingUnit = false;
        SystemMonitorAction.SelectedIndex = ToComboIndex(_settings.SystemMonitorAction);
        UpdateMonitorItemsSummary();

        UpdateCheckToggle.IsChecked = _settings.CheckForUpdatesAutomatically;
        ShowInTaskbarToggle.IsChecked = _settings.ShowInTaskbar;
        StartMinimizedToTrayToggle.IsChecked = _settings.StartMinimizedToTray;
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;
        WipeMethodCombo.SelectedIndex = _settings.SecureDeleteMethod switch
        {
            WipeMethod.Dod522022M => 1,
            WipeMethod.Dod522022MEce => 2,
            WipeMethod.PeterGutmann => 3,
            WipeMethod.Custom => 4,
            _ => 0
        };
        ExclusionsBox.Text = string.Join("\n", _settings.ExcludedPaths);
        _loading = false;
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

    /// <summary>Reinterprets the entered value when the user switches GB↔MB, and
    /// adjusts the NumberBox range for the chosen unit.</summary>
    private void FreeSpaceUnit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUnit) return;
        var newIsMb = FreeSpaceUnit.SelectedIndex == 1;
        if (newIsMb == _unitIsMb) return;
        var current = double.IsNaN(FreeSpaceBox.Value) ? 0 : FreeSpaceBox.Value;
        ApplyFreeSpaceBounds(newIsMb);
        var converted = RoundForUnit(newIsMb ? current * MbPerGb : current / MbPerGb, newIsMb);
        FreeSpaceBox.Value = Math.Clamp(converted, FreeSpaceBox.Minimum, FreeSpaceBox.Maximum);
        _unitIsMb = newIsMb;
        if (!_loading) _ = PersistAsync();
    }

    // Instant-save handlers: every control persists on change, so there is no
    // Save button. Populating the controls (load / restore defaults) sets _loading
    // to suppress these.
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void Setting_ComboChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void Setting_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void FreeSpace_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) { if (!_loading) _ = PersistAsync(); }

    private void ApplyFreeSpaceBounds(bool isMb)
    {
        if (isMb)
        {
            FreeSpaceBox.Minimum = 50; FreeSpaceBox.Maximum = 102400;
            FreeSpaceBox.SmallChange = 50; FreeSpaceBox.LargeChange = 500;
            // Whole megabytes only: an integer formatter keeps the displayed text in
            // sync with the value. Without an explicit formatter the default one can
            // render a converted value like 549.99 oddly, which desyncs the text from
            // the value and leaves the box unable to accept typed input.
            FreeSpaceBox.NumberFormatter = MakeFormatter(0);
        }
        else
        {
            FreeSpaceBox.Minimum = 0.1; FreeSpaceBox.Maximum = 100;
            FreeSpaceBox.SmallChange = 0.1; FreeSpaceBox.LargeChange = 1;
            FreeSpaceBox.NumberFormatter = MakeFormatter(2);
        }
    }

    /// <summary>A plain decimal formatter with a fixed number of fraction digits and
    /// no digit grouping, so the NumberBox never shows a stray leading zero or a long
    /// floating-point tail after a unit conversion.</summary>
    private static DecimalFormatter MakeFormatter(int fractionDigits) => new()
    {
        IntegerDigits = 1,
        FractionDigits = fractionDigits,
        IsGrouped = false
    };

    /// <summary>Rounds a value for display in the given unit: whole megabytes, or
    /// gigabytes to two decimals.</summary>
    private static double RoundForUnit(double value, bool isMb) => isMb ? Math.Round(value) : Math.Round(value, 2);

    /// <summary>The Safe Windows categories the monitor may clean, with each one's
    /// current tick state: the user's saved selection when configured, otherwise the
    /// categories enabled on the Windows Cleanup page (the monitor's default set).</summary>
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

    /// <summary>Lets the user pick exactly which Safe categories the low-disk-space
    /// monitor cleans. The chosen set is saved immediately; an empty set means the
    /// monitor cleans nothing.</summary>
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
            Title = "System monitor - categories to clean",
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
        StatusText.Text = "Monitor items saved.";
    }

    /// <summary>Reads every control into settings, persists, and applies side
    /// effects. Called on any change (instant save) and by Restore Defaults.</summary>
    private async Task PersistAsync()
    {
        _settings.SystemMonitoringEnabled = SystemMonitoringToggle.IsChecked == true;
        var mb = FreeSpaceUnit.SelectedIndex == 1;
        var entered = double.IsNaN(FreeSpaceBox.Value) ? (mb ? MbPerGb : 1.0) : FreeSpaceBox.Value;
        _settings.SystemMonitorFreeSpaceGb = Math.Clamp(mb ? entered / MbPerGb : entered, 0.01, 100);
        _settings.SystemMonitorFreeSpaceUnit = mb ? "MB" : "GB";
        _settings.SystemMonitorAction = FromComboIndex(SystemMonitorAction.SelectedIndex);

        _settings.CheckForUpdatesAutomatically = UpdateCheckToggle.IsChecked == true;
        _settings.ShowInTaskbar = ShowInTaskbarToggle.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedToTrayToggle.IsChecked == true;
        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        _settings.SecureDeleteMethod = WipeMethodCombo.SelectedIndex switch
        {
            1 => WipeMethod.Dod522022M,
            2 => WipeMethod.Dod522022MEce,
            3 => WipeMethod.PeterGutmann,
            4 => WipeMethod.Custom,
            _ => WipeMethod.SimpleZeroFill
        };
        _settings.ExcludedPaths = ExclusionsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();

        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyShowInTaskbar(_settings.ShowInTaskbar);
            mainWindow.ApplyMinimizeToTray(_settings.MinimizeToTray);
            mainWindow.ApplyCloseToTray(_settings.CloseToTray);
        }

        // Start or stop the background agent (and Windows startup) to match the
        // services now enabled - here, the low-disk-space monitor.
        (App.Current as App)?.ApplyBackgroundServices(_settings);

        StatusText.Text = "Saved.";
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        // Populate the controls with defaults without firing a save per control,
        // then persist once at the end.
        _loading = true;
        _loadingUnit = true;
        SystemMonitoringToggle.IsChecked = defaults.SystemMonitoringEnabled;
        _unitIsMb = false;
        FreeSpaceUnit.SelectedIndex = 0;
        ApplyFreeSpaceBounds(false);
        FreeSpaceBox.Value = defaults.SystemMonitorFreeSpaceGb;
        _loadingUnit = false;
        SystemMonitorAction.SelectedIndex = 1;
        _settings.SystemMonitorCategories = defaults.SystemMonitorCategories; // back to the default set
        UpdateMonitorItemsSummary();
        UpdateCheckToggle.IsChecked = defaults.CheckForUpdatesAutomatically;
        ShowInTaskbarToggle.IsChecked = defaults.ShowInTaskbar;
        StartMinimizedToTrayToggle.IsChecked = defaults.StartMinimizedToTray;
        CloseToTrayToggle.IsChecked = defaults.CloseToTray;
        MinimizeToTrayToggle.IsChecked = defaults.MinimizeToTray;
        WipeMethodCombo.SelectedIndex = 0;
        ExclusionsBox.Text = "";
        _loading = false;

        await PersistAsync();
        StatusText.Text = "Defaults restored.";
    }
}
