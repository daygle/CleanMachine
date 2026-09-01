using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class CleanerPage : Page
{
    private readonly BrowserCleanupService _service = new();
    private CancellationTokenSource? _cancel;

    public CleanerPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CheckInterruptedAsync();
    }

    private async Task CheckInterruptedAsync()
    {
        var state = await _service.LoadInterruptedStateAsync();
        if (state is not null)
        {
            StatusText.Text = $"A previous cleanup ({state.Removed} files removed) was interrupted. " +
                              $"Recovered: {state.BytesRecovered:N0} bytes. {state.RemainingFiles.Count} files remain.";
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        _cancel = new CancellationTokenSource();
        Progress.Visibility = Visibility.Visible;
        try
        {
            var settings = await AppSettings.LoadAsync(_cancel.Token);
            var targets = await _service.ScanAsync(
                ["chrome", "edge", "firefox"],
                additionalRoots: null,
                excludedPaths: settings.ExcludedPaths,
                token: _cancel.Token);

            if (targets.Count == 0) { StatusText.Text = "No supported browser cache folders were found."; return; }

            var running = BrowserCleanupService.GetRunningBrowsers();
            if (running.Count > 0)
            {
                StatusText.Text = $"Close {string.Join(", ", running)} before cleaning.";
                return;
            }

            var list = new StackPanel { Spacing = 8 };
            foreach (var target in targets)
                list.Children.Add(new CheckBox
                {
                    IsChecked = target.Selected,
                    Content = $"{target.Browser} · {target.Category} · {target.Bytes:N0} bytes",
                    Tag = target
                });

            var dialog = new ContentDialog
            {
                Title = "Review browser cleanup",
                Content = list,
                PrimaryButtonText = "Clean selected",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var selected = list.Children.OfType<CheckBox>()
                .Select(c => ((BrowserCleanupTarget)c.Tag!) with { Selected = c.IsChecked == true });

            var progress = new Progress<CleanupProgress>(p =>
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total);

            var result = await _service.CleanWithReportAsync(
                selected,
                new BrowserCleanupOptions(
                    ExcludedPaths: settings.ExcludedPaths,
                    RequireBrowsersClosed: true),
                progress,
                _cancel.Token);

            StatusText.Text = $"Complete: {result.Result.ItemsRemoved:N0} files removed, " +
                              $"{result.Result.BytesRecovered:N0} bytes recovered, " +
                              $"{result.Skipped.Count:N0} skipped.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Cleanup cancelled."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            ScanButton.IsEnabled = true;
            _cancel?.Dispose();
            _cancel = null;
        }
    }
}
