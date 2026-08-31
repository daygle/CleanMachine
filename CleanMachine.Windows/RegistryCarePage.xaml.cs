using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class RegistryCarePage : Page
{
    private readonly RegistryCareService _service = new();
    public RegistryCarePage() => InitializeComponent();
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var review = await _service.ScanAsync(); if (review.Findings.Count == 0) { StatusText.Text = "No low-risk findings were found."; return; }
        var list = new StackPanel { Spacing = 8 }; foreach (var item in review.Findings) list.Children.Add(new CheckBox { IsChecked = true, Content = $"{item.Path} · {item.Confidence}% confidence · {item.Reason}", Tag = item });
        var dialog = new ContentDialog { Title = "Review registry findings", Content = list, PrimaryButtonText = "Create backup", CloseButtonText = "Cancel", XamlRoot = XamlRoot }; if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selected = list.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (RegistryFinding)x.Tag!); var result = await _service.PrepareReviewAsync(selected); StatusText.Text = $"Backup created: {result.Backup?.FilePath}. No registry values were changed.";
    }
}
