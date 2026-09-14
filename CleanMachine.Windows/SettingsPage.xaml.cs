using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();
    // Guards the unit ComboBox while settings are loaded into the controls.
    private bool _loadingUnit;
    private bool _unitIsMb;

    private const double MbPerGb = 1024.0;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();

        BackgroundAgentToggle.IsChecked = _settings.BackgroundAgentEnabled;
        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        _loadingUnit = true;
        _unitIsMb = string.Equals(_settings.SystemMonitorFreeSpaceUnit, "MB", StringComparison.OrdinalIgnoreCase);
        FreeSpaceUnit.SelectedIndex = _unitIsMb ? 1 : 0;
        ApplyFreeSpaceBounds(_unitIsMb);
        FreeSpaceBox.Value = _unitIsMb ? _settings.SystemMonitorFreeSpaceGb * MbPerGb : _settings.SystemMonitorFreeSpaceGb;
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
        FreeSpaceBox.Value = newIsMb ? current * MbPerGb : current / MbPerGb;
        _unitIsMb = newIsMb;
    }

    private void ApplyFreeSpaceBounds(bool isMb)
    {
        if (isMb)
        {
            FreeSpaceBox.Minimum = 50; FreeSpaceBox.Maximum = 102400;
            FreeSpaceBox.SmallChange = 50; FreeSpaceBox.LargeChange = 500;
        }
        else
        {
            FreeSpaceBox.Minimum = 0.1; FreeSpaceBox.Maximum = 100;
            FreeSpaceBox.SmallChange = 0.1; FreeSpaceBox.LargeChange = 1;
        }
    }

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
                ? "No items selected — nothing will be cleaned."
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
            Title = "System monitor — categories to clean",
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundAgentEnabled = BackgroundAgentToggle.IsChecked == true;
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

        try
        {
            StartupRegistration.SetEnabled(
                _settings.BackgroundAgentEnabled,
                Environment.ProcessPath ?? string.Empty);
        }
        catch { /* startup registration is best-effort */ }

        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyShowInTaskbar(_settings.ShowInTaskbar);
            mainWindow.ApplyMinimizeToTray(_settings.MinimizeToTray);
            mainWindow.ApplyCloseToTray(_settings.CloseToTray);
        }

        // Apply the agent state: start or stop the background agent to match the toggle.
        if (App.Current is App app)
        {
            if (_settings.BackgroundAgentEnabled)
                app.StartBackgroundAgent(_settings);
            else
                app.StopBackgroundAgent();
        }

        StatusText.Text = "Settings saved.";
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        BackgroundAgentToggle.IsChecked = defaults.BackgroundAgentEnabled;
        SystemMonitoringToggle.IsChecked = defaults.SystemMonitoringEnabled;
        _loadingUnit = true;
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
        StatusText.Text = "Defaults restored. Click Save Settings to apply.";
    }
}
