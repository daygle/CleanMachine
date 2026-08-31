using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class OverviewPage : Page
{
    public OverviewPage() => InitializeComponent();
    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<CleanerPage>();
    private void OpenRegistry_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<RegistryCarePage>();
    private void OpenWindows_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<WindowsCleanupPage>();
    private void OpenUpdates_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.MainWindow!).Navigate<UpdatesPage>();
}
