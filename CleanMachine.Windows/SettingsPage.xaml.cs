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

        AgentToggle.IsChecked = _settings.BackgroundAgentEnabled;
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

        SystemMonitoringToggle.IsChecked = _settings.SystemMonitoringEnabled;
        FreeSpaceBox.Value = _settings.SystemMonitorFreeSpaceGb;
        SystemMonitorAction.SelectedIndex = ToComboIndex(_settings.SystemMonitorAction);

        UpdateCheckToggle.IsChecked = _settings.CheckForUpdatesAutomatically;
        ShowInTaskbarToggle.IsChecked = _settings.ShowInTaskbar;
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
        _settings.BackgroundAgentEnabled = AgentToggle.IsChecked == true;
        _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true;

        ApplyMonitor("chrome", ChromeEnabled, ChromeAction);
        ApplyMonitor("edge", EdgeEnabled, EdgeAction);
        ApplyMonitor("firefox", FirefoxEnabled, FirefoxAction);

        _settings.SystemMonitoringEnabled = SystemMonitoringToggle.IsChecked == true;
        _settings.SystemMonitorFreeSpaceGb = Math.Clamp(FreeSpaceBox.Value, 0.1, 100);
        _settings.SystemMonitorAction = FromComboIndex(SystemMonitorAction.SelectedIndex);

        _settings.CheckForUpdatesAutomatically = UpdateCheckToggle.IsChecked == true;
        _settings.ShowInTaskbar = ShowInTaskbarToggle.IsChecked == true;
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
            mainWindow.ApplyShowInTaskbar(_settings.ShowInTaskbar);

        // Restart the agent so enabling/disabling it takes effect immediately.
        // Its handlers reload settings on every event, so action changes apply
        // live either way.
        if (App.Current is App app)
        {
            if (_settings.BackgroundAgentEnabled)
                app.StartBackgroundAgent(_settings);
            else
                app.StopBackgroundAgent();
        }

        StatusText.Text = "Settings saved.";
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

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        AgentToggle.IsChecked = defaults.BackgroundAgentEnabled;
        CleanToggle.IsChecked = defaults.CleanOnBrowserExit;
        ChromeEnabled.IsChecked = true;   ChromeAction.SelectedIndex = 2;
        EdgeEnabled.IsChecked = true;     EdgeAction.SelectedIndex = 2;
        FirefoxEnabled.IsChecked = true;  FirefoxAction.SelectedIndex = 2;
        SystemMonitoringToggle.IsChecked = defaults.SystemMonitoringEnabled;
        FreeSpaceBox.Value = defaults.SystemMonitorFreeSpaceGb;
        SystemMonitorAction.SelectedIndex = 1;
        UpdateCheckToggle.IsChecked = defaults.CheckForUpdatesAutomatically;
        ShowInTaskbarToggle.IsChecked = defaults.ShowInTaskbar;
        WipeMethodCombo.SelectedIndex = 0;
        ExclusionsBox.Text = "";
        StatusText.Text = "Defaults restored. Click Save Settings to apply.";
    }
}
