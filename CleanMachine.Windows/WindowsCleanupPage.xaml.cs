using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanMachine.Windows;
public sealed partial class WindowsCleanupPage : Page
{
    private readonly WindowsCleanupService _service = new(); private CancellationTokenSource? _cancel;
    public WindowsCleanupPage() => InitializeComponent();
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.Visibility = Visibility.Visible; _cancel = new CancellationTokenSource();
        try
        {
            var findings = await _service.ScanAsync(_cancel.Token); FindingList.ItemsSource = findings.Select(f => $"{f.Name} · {f.Bytes:N0} bytes · {f.Risk}"); var list = new StackPanel { Spacing = 8 }; foreach (var finding in findings) list.Children.Add(new CheckBox { IsChecked = finding.Selected, Content = $"{finding.Name} · {finding.Bytes:N0} bytes · {finding.Risk}", Tag = finding });
            var dialog = new ContentDialog { Title = "Review Windows cleanup", Content = list, PrimaryButtonText = "Clean selected", CloseButtonText = "Cancel", XamlRoot = XamlRoot }; if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = list.Children.OfType<CheckBox>().Select(c => ((WindowsCleanupFinding)c.Tag!) with { Selected = c.IsChecked == true }); var progress = new Progress<CleanupProgress>(p => { Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total; StatusText.Text = $"{p.Phase}: {p.Completed}/{p.Total}"; }); var result = await _service.CleanSelectedAsync(selected, progress, _cancel.Token); StatusText.Text = $"Complete: {result.Result.ItemsRemoved:N0} files removed, {result.Result.BytesRecovered:N0} bytes recovered, {result.Skipped.Count:N0} skipped.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Windows cleanup cancelled."; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { ScanButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancel.Dispose(); _cancel = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
