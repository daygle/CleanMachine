using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class WindowsCleanupPage : Page
{
    private static readonly string[] GroupOrder =
        ["Windows Explorer", "Windows System", "Windows Advanced Options", "Windows Downloads"];

    private readonly WindowsCleanupService _service = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _settings = new();
    private CancellationTokenSource? _cancel;

    public WindowsCleanupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        BuildPanel();
    }

    private void BuildPanel()
    {
        CategoryPanel.Children.Clear();
        foreach (var group in WindowsCleanupService.Catalog
                     .GroupBy(c => c.Group)
                     .OrderBy(g => GroupIndex(g.Key)))
        {
            CategoryPanel.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)),
                Margin = new Thickness(0, 12, 0, 2)
            });
            foreach (var category in group.OrderBy(c => c.Name))
            {
                var box = new CheckBox
                {
                    Content = category.Name,
                    IsChecked = WindowsCleanupService.IsEnabled(category, _settings),
                    Tag = category
                };
                box.Checked += (_, _) => SetEnabled(category, true);
                box.Unchecked += (_, _) => SetEnabled(category, false);
                CategoryPanel.Children.Add(box);
            }
        }
    }

    private static int GroupIndex(string group)
    {
        var index = Array.IndexOf(GroupOrder, group);
        return index < 0 ? int.MaxValue : index;
    }

    private void SetEnabled(CleanupCategory category, bool enabled)
    {
        if (enabled)
        {
            _settings.EnabledCleanupCategories.Add(category.Id);
            _settings.DisabledCleanupCategories.Remove(category.Id);
        }
        else
        {
            _settings.EnabledCleanupCategories.Remove(category.Id);
            _settings.DisabledCleanupCategories.Add(category.Id);
        }
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        await _saveGate.WaitAsync();
        try { await _settings.SaveAsync(); }
        finally { _saveGate.Release(); }
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = _service.Scan(_settings.ExcludedPaths);
            var enabled = items.Where(i => WindowsCleanupService.IsEnabled(i.Category, _settings)).ToArray();
            var fileBytes = enabled.Where(i => i.Category.Kind == CleanupKind.Files).Sum(i => i.Bytes);
            var historyEntries = enabled.Where(i => i.Category.Kind == CleanupKind.RegistryValues).Sum(i => i.Bytes);
            StatusText.Text =
                $"Scan complete: {fileBytes:N0} bytes of cache/temp files and {historyEntries:N0} history entries can be removed from {enabled.Length:N0} enabled items.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var enabled = WindowsCleanupService.Catalog.Where(c => WindowsCleanupService.IsEnabled(c, _settings)).ToArray();
        if (enabled.Length == 0) { StatusText.Text = "No items are enabled. Select at least one item to clean."; return; }

        var preview = _service.BuildPreview(enabled, _settings.ExcludedPaths);
        if (preview.TotalItems == 0) { StatusText.Text = "No items were found to clean in the enabled categories."; return; }

        var review = enabled.Where(c => c.Risk != CleanupRisk.Safe).ToArray();
        if (!await ConfirmPreviewAsync(preview, review)) { StatusText.Text = "Cleanup was not confirmed."; return; }

        CleanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        _cancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CleanupProgress>(p =>
            {
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                StatusText.Text = $"Cleaning {p.Phase}: {p.Completed}/{p.Total}";
            });
            var result = await _service.CleanSelectedAsync(
                enabled,
                new WindowsCleanupOptions(ConfirmReviewCategories: true, ExcludedPaths: _settings.ExcludedPaths),
                progress,
                _cancel.Token);
            StatusText.Text =
                $"Complete: {result.Result.ItemsRemoved:N0} items removed, {result.Result.BytesRecovered:N0} bytes recovered, {result.Skipped.Count:N0} skipped.";
            if (result.Skipped.Count > 0) StatusText.Text += $" First: {result.Skipped[0].Reason}.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Windows cleanup cancelled."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            CleanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cancel?.Dispose();
            _cancel = null;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> ConfirmPreviewAsync(CleanupPreview preview, IReadOnlyList<CleanupCategory> review)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var item in preview.Items)
        {
            var size = item.Bytes > 0 ? $" ({FormatBytes(item.Bytes)})" : "";
            panel.Children.Add(new TextBlock
            {
                Text = $"{item.Category} — {item.Description}{size}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x53, 0x63, 0x5B))
            });
        }
        if (preview.Items.Count < preview.TotalItems)
            panel.Children.Add(new TextBlock
            {
                Text = $"…and {preview.TotalItems - preview.Items.Count:N0} more items will be cleaned.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
        if (review.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"Warning: {string.Join(", ", review.Select(c => c.Name))} can remove data permanently.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
            });

        var dialog = new ContentDialog
        {
            Title = $"Review {preview.TotalItems:N0} items before cleaning",
            Content = new ScrollViewer { MaxHeight = 320, Content = panel },
            PrimaryButtonText = "Clean Now",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
