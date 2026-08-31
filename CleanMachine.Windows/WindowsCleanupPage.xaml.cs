using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class WindowsCleanupPage : Page
{
    private readonly WindowsCleanupService _service = new();
    private CancellationTokenSource? _cancel;

    public WindowsCleanupPage() => InitializeComponent();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.Visibility = Visibility.Visible; _cancel = new CancellationTokenSource();
        try
        {
            var findings = await _service.ScanAsync(_cancel.Token);
            var list = new StackPanel { Spacing = 8 };
            foreach (var finding in findings)
            {
                var description = finding.Risk == CleanupRisk.Safe ? finding.Description : $"{finding.Description} — requires explicit confirmation";
                list.Children.Add(new CheckBox { IsChecked = finding.Selected, Content = $"{finding.Name} · {finding.Bytes:N0} bytes · {finding.Risk}\n{description}", Tag = finding });
            }
            FindingList.ItemsSource = findings.Select(f => $"{f.Name} · {f.Bytes:N0} bytes · {f.Risk}");
            var dialog = new ContentDialog { Title = "Review Windows cleanup", Content = list, PrimaryButtonText = "Clean selected", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = list.Children.OfType<CheckBox>().Select(c => ((WindowsCleanupFinding)c.Tag!) with { Selected = c.IsChecked == true }).ToArray();
            var reviewSelected = selected.Any(f => f.Selected && f.Risk != CleanupRisk.Safe);
            var confirmed = !reviewSelected || await ConfirmReviewCategoriesAsync();
            if (!confirmed) { StatusText.Text = "Review categories were not selected."; return; }
            var progress = new Progress<CleanupProgress>(p => { Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total; StatusText.Text = $"{p.Phase}: {p.Completed}/{p.Total}"; });
            var result = await _service.CleanSelectedAsync(selected, new WindowsCleanupOptions(confirmed), progress, _cancel.Token);
            StatusText.Text = $"Complete: {result.Result.ItemsRemoved:N0} items removed, {result.Result.BytesRecovered:N0} bytes recovered, {result.Skipped.Count:N0} skipped.";
            if (result.Skipped.Count > 0) StatusText.Text += $" First issue: {result.Skipped[0].Reason}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Windows cleanup cancelled."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { ScanButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancel?.Dispose(); _cancel = null; }
    }

    private async Task<bool> ConfirmReviewCategoriesAsync()
    {
        var dialog = new ContentDialog { Title = "Confirm advanced cleanup", Content = "Recycle Bin removal is permanent. Windows Update cleanup may require administrator rights and is currently review-only. Continue?", PrimaryButtonText = "I understand", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
