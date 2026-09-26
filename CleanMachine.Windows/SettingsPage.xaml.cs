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
        // The in-app uninstall flow exists only for MSIX: the .exe flavor's
        // uninstaller already closes the app, cleans up, and asks about data.
        if (ScheduleService.IsMsix) UninstallSection.Visibility = Visibility.Visible;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _settings = await AppSettings.LoadAsync();

        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
        ShowInTaskbarToggle.IsChecked = _settings.ShowInTaskbar;
        StartMinimizedToTrayToggle.IsChecked = _settings.StartMinimizedToTray;
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;
        AlwaysShowTrayToggle.IsChecked = _settings.AlwaysShowTray;
        TrayCleaningAnimationToggle.IsChecked = _settings.TrayCleaningAnimation;
        ExclusionsBox.Text = string.Join("\n", _settings.ExcludedPaths);
        _loading = false;
    }

    // Instant-save handlers: every control persists on change, so there is no
    // Save button. Populating the controls (load / restore defaults) sets _loading
    // to suppress these.
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }
    private void Setting_LostFocus(object sender, RoutedEventArgs e) { if (!_loading) _ = PersistAsync(); }

    /// <summary>Reads every control into settings, persists, and applies side
    /// effects. Called on any change (instant save) and by Restore Defaults.</summary>
    private async Task PersistAsync()
    {
        _settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
        _settings.ShowInTaskbar = ShowInTaskbarToggle.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedToTrayToggle.IsChecked == true;
        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        _settings.AlwaysShowTray = AlwaysShowTrayToggle.IsChecked == true;
        _settings.TrayCleaningAnimation = TrayCleaningAnimationToggle.IsChecked == true;
        _settings.ExcludedPaths = ExclusionsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();

        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyShowInTaskbar(_settings.ShowInTaskbar);
            mainWindow.ApplyMinimizeToTray(_settings.MinimizeToTray);
            mainWindow.ApplyCloseToTray(_settings.CloseToTray);
            mainWindow.ApplyAlwaysShowTray(_settings.AlwaysShowTray);
            mainWindow.ApplyTrayCleaningAnimation(_settings.TrayCleaningAnimation);
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
        StartWithWindowsToggle.IsChecked = defaults.StartWithWindows;
        ShowInTaskbarToggle.IsChecked = defaults.ShowInTaskbar;
        StartMinimizedToTrayToggle.IsChecked = defaults.StartMinimizedToTray;
        CloseToTrayToggle.IsChecked = defaults.CloseToTray;
        MinimizeToTrayToggle.IsChecked = defaults.MinimizeToTray;
        AlwaysShowTrayToggle.IsChecked = defaults.AlwaysShowTray;
        TrayCleaningAnimationToggle.IsChecked = defaults.TrayCleaningAnimation;
        ExclusionsBox.Text = "";
        _loading = false;

        await PersistAsync();
        StatusText.Text = "Defaults restored.";
    }

    /// <summary>MSIX uninstall flow: confirm, ask about the data folder (keeping
    /// it is the default, matching the .exe installer), let
    /// <see cref="MsixUninstallService"/> clean everything outside the package
    /// and start the deferred package removal, then exit so the helper can run.</summary>
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Uninstall CleanMachine?",
            Content = "The app will close and the package will be removed for this user.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var data = new ContentDialog
        {
            Title = "Also delete your CleanMachine data?",
            Content = "Settings, cleanup statistics, activity history, and registry backup files. " +
                      "Keep them and a future reinstall picks up where you left off.",
            PrimaryButtonText = "Delete data",
            SecondaryButtonText = "Keep data",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = XamlRoot
        };
        var dataChoice = await data.ShowAsync();
        if (dataChoice == ContentDialogResult.None) return; // Cancel aborts the uninstall
        var removeData = dataChoice == ContentDialogResult.Primary;

        UninstallButton.IsEnabled = false;
        var started = await Task.Run(() => MsixUninstallService.Start(removeData));
        if (!started)
        {
            UninstallButton.IsEnabled = true;
            var failed = new ContentDialog
            {
                Title = "Could not start the removal",
                Content = "Nothing was changed. Uninstall through Windows Settings > Apps instead.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await failed.ShowAsync();
            return;
        }

        // The package removal runs in the deferred helper once this process exits.
        if (App.MainWindow is MainWindow mainWindow) mainWindow.RequestExit();
        else Application.Current.Exit();
    }
}
