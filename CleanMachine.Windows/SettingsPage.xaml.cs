using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();

        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        FreeSpaceBox.Value = _settings.SystemMonitorFreeSpaceGb;
        SystemMonitorAction.SelectedIndex = ToComboIndex(_settings.SystemMonitorAction);

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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.SystemMonitoringEnabled = SystemMonitoringToggle.IsChecked == true;
        _settings.SystemMonitorFreeSpaceGb = Math.Clamp(FreeSpaceBox.Value, 0.1, 100);
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
        catch { /* startup registration is best-effort; the toggle lives on Browser Cleaner */ }

        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyShowInTaskbar(_settings.ShowInTaskbar);
            mainWindow.ApplyMinimizeToTray(_settings.MinimizeToTray);
            mainWindow.ApplyCloseToTray(_settings.CloseToTray);
        }

        // Re-apply the agent state from settings so an externally changed agent
        // flag (Browser Cleaner page) takes effect after any settings save.
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
        SystemMonitoringToggle.IsChecked = defaults.SystemMonitoringEnabled;
        FreeSpaceBox.Value = defaults.SystemMonitorFreeSpaceGb;
        SystemMonitorAction.SelectedIndex = 1;
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
