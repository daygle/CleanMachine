using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();
    // Suppresses instant-save while settings are being loaded into the controls
    // (or reset by Restore Defaults), so populating them does not save. Starts
    // true so SelectionChanged events fired during InitializeComponent (from XAML
    // SelectedIndex) cannot save default values over the real settings file before
    // LoadAsync has run.
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _settings = await AppSettings.LoadAsync();

        UpdateCheckToggle.IsChecked = _settings.CheckForUpdatesAutomatically;
        SkipUpdateConfirmToggle.IsChecked = _settings.SkipUpdateConfirmation;
        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
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

    // Instant-save handlers: every control persists on change, so there is no
    // Save button. Populating the controls (load / restore defaults) sets _loading
    // to suppress these.
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void Setting_ComboChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void Setting_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }

    /// <summary>Reads every control into settings, persists, and applies side
    /// effects. Called on any change (instant save) and by Restore Defaults.</summary>
    private async Task PersistAsync()
    {
        _settings.CheckForUpdatesAutomatically = UpdateCheckToggle.IsChecked == true;
        _settings.SkipUpdateConfirmation = SkipUpdateConfirmToggle.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
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

        // Keep Windows startup registration (and the background agent) in step with
        // the "Start with Windows" preference set here.
        (App.Current as App)?.ApplyBackgroundServices(_settings);

        StatusText.Text = "Saved.";
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        // Populate the controls with defaults without firing a save per control,
        // then persist once at the end.
        _loading = true;
        UpdateCheckToggle.IsChecked = defaults.CheckForUpdatesAutomatically;
        SkipUpdateConfirmToggle.IsChecked = defaults.SkipUpdateConfirmation;
        StartWithWindowsToggle.IsChecked = defaults.StartWithWindows;
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
