using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class SettingsPage : Page
{
    private AppSettings _settings = new();
    public SettingsPage() { InitializeComponent(); Loaded += async (_, _) => await LoadAsync(); }
    private async Task LoadAsync() { _settings = await AppSettings.LoadAsync(); AgentToggle.IsChecked = _settings.BackgroundAgentEnabled; CleanToggle.IsChecked = _settings.CleanOnBrowserExit; }
    private async void Save_Click(object sender, RoutedEventArgs e) { _settings.BackgroundAgentEnabled = AgentToggle.IsChecked == true; _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true; await _settings.SaveAsync(); try { StartupRegistration.SetEnabled(_settings.BackgroundAgentEnabled, Environment.ProcessPath ?? string.Empty); } catch { } StatusText.Text = "Settings saved."; }
}
