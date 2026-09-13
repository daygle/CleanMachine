using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class OverviewPage : Page
{
    // Guards the Toggled event while we set the initial state from settings.
    private bool _ready;
    private CancellationTokenSource? _updateCts;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await LoadAgentAsync();
            _ = CheckForUpdatesAsync();
        };
    }

    private async Task LoadAgentAsync()
    {
        var settings = await AppSettings.LoadAsync();
        _ready = false;
        AgentToggle.IsOn = settings.BackgroundAgentEnabled;
        _ready = true;
        ShowAgentState(settings.BackgroundAgentEnabled);
    }

    /// <summary>Performs a background update check when the setting is enabled.
    /// Shows a brief banner while checking, then either hides it (up to date)
    /// or shows the result. Always clears the checking state when done.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var settings = await AppSettings.LoadAsync();
            if (!settings.CheckForUpdatesAutomatically) return;

            UpdateBanner.Visibility = Visibility.Visible;
            UpdateIcon.Glyph = "\uE946"; // Sync
            UpdateStatusText.Text = "Checking for updates…";
            UpdateDetailText.Text = "";
            UpdateActionButton.Visibility = Visibility.Collapsed;

            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();
            var ct = _updateCts.Token;

            var service = new UpdateService();
            var result = await Task.Run(() => service.CheckAsync(ct), ct);

            if (ct.IsCancellationRequested) return;

            if (result.Error is not null)
            {
                // Silent failure: just hide the banner.
                UpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            if (!result.Available)
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }

            // Update available: show it.
            UpdateIcon.Glyph = "\uE733"; // ArrowUp
            UpdateIcon.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58));
            UpdateStatusText.Text = $"Update available: v{result.Manifest!.Version}";
            UpdateDetailText.Text = result.Manifest.ReleaseNotes;
            UpdateActionButton.Content = "View";
            UpdateActionButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Silent failure: hide the banner so it never gets stuck.
            UpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
        => ((MainWindow)App.MainWindow!).Navigate<UpdatesPage>();

    private async void AgentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var enabled = AgentToggle.IsOn;
        var settings = await AppSettings.LoadAsync();
        settings.BackgroundAgentEnabled = enabled;
        await settings.SaveAsync();

        try { StartupRegistration.SetEnabled(enabled, Environment.ProcessPath ?? string.Empty); }
        catch { /* startup registration is best-effort */ }

        if (App.Current is App app)
        {
            if (enabled) app.StartBackgroundAgent(settings);
            else app.StopBackgroundAgent();
        }
        ShowAgentState(enabled);
    }

    private void ShowAgentState(bool enabled)
    {
        AgentStatusText.Text = enabled ? "●  Background Agent  ON" : "○  Background Agent  OFF";
        AgentStatusText.Foreground = new SolidColorBrush(enabled
            ? global::Windows.UI.Color.FromArgb(255, 0x25, 0x42, 0x39)
            : global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F));
    }

    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<CleanerPage>();
    private void OpenRegistry_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<RegistryCarePage>();
    private void OpenWindows_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<WindowsCleanupPage>();
    private void OpenUpdates_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<UpdatesPage>();
}
