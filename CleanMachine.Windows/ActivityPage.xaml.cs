using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class ActivityPage : Page
{
    private readonly ActivityStore _store = new();
    public ActivityPage() { InitializeComponent(); Loaded += async (_, _) => await LoadAsync(); }
    private async Task LoadAsync() { var items = await _store.LoadAsync(); ActivityList.ItemsSource = items.Select(x => $"{x.Time:g} · {x.Title} · {x.Detail}"); StatusText.Text = items.Count == 0 ? "No activity recorded yet." : $"{items.Count} recent activities"; }
    private async void Clear_Click(object sender, RoutedEventArgs e) { await _store.ClearAsync(); await LoadAsync(); }
}
