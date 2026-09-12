using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class OverviewPage : Page
{
    // Guards the Toggled event while we set the initial state from settings.
    private bool _ready;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAgentAsync();
    }

    private async Task LoadAgentAsync()
    {
        var settings = await AppSettings.LoadAsync();
        _ready = false;
        AgentToggle.IsOn = settings.BackgroundAgentEnabled;
        _ready = true;
        ShowAgentState(settings.BackgroundAgentEnabled);
    }

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
