using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class UpdatesPage : Page
{
    private readonly UpdateService _service = new();
    public UpdatesPage() => InitializeComponent();
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var result = await _service.CheckAsync(); if (result.Error is not null) { StatusText.Text = result.Error; return; } if (!result.Available) { StatusText.Text = "You are running the latest version."; return; }
        var dialog = new ContentDialog { Title = $"Version {result.Manifest!.Version} available", Content = result.Manifest.ReleaseNotes, PrimaryButtonText = "Download", CloseButtonText = "Later", XamlRoot = XamlRoot }; if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { var path = await _service.DownloadAndVerifyAsync(result.Package!); StatusText.Text = $"Verified package staged at {path}. Installation requires explicit confirmation."; } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
