using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); Navigate<OverviewPage>(); _ = LoadAgentStateAsync(); }
    public void Navigate<T>() where T : Page, new() => ContentFrame.Navigate(typeof(T));
    private async Task LoadAgentStateAsync() { var settings = await AppSettings.LoadAsync(); AgentStatusText.Text = settings.BackgroundAgentEnabled ? "●  Background agent  ON" : "●  Background agent  OFF"; }
    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate<OverviewPage>();
    private void Cleaner_Click(object sender, RoutedEventArgs e) => Navigate<CleanerPage>();
    private void Registry_Click(object sender, RoutedEventArgs e) => Navigate<RegistryCarePage>();
    private void WindowsCleanup_Click(object sender, RoutedEventArgs e) => Navigate<WindowsCleanupPage>();
    private void SecureDeleteNav_Click(object sender, RoutedEventArgs e) => Navigate<SecureDeletePage>();
    private void Activity_Click(object sender, RoutedEventArgs e) => Navigate<ActivityPage>();
    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate<SettingsPage>();
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => Navigate<UpdatesPage>();
}
