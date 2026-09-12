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
    private CleanupPreview? _lastPreview;

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
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)),
                Margin = new Thickness(0, 12, 0, 2)
            });
            foreach (var category in group.OrderBy(c => c.Name))
            {
                var box = new CheckBox
                {
                    Content = category.Name,
                    IsChecked = WindowsCleanupService.IsEnabled(category, _settings),
                    Tag = category,
                    MinHeight = 30
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

    private IReadOnlyList<CleanupCategory> EnabledCategories() =>
        WindowsCleanupService.Catalog.Where(c => WindowsCleanupService.IsEnabled(c, _settings)).ToArray();

    /// <summary>Analysis: measure every enabled category and render a CCleaner-style
    /// report (one row per category with its size and item count). Nothing is removed.</summary>
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCategories();
        if (enabled.Length == 0)
        {
            ReportHeadline.Text = "Nothing selected.";
            StatusText.Text = "Select at least one item on the left to analyze.";
            return;
        }

        AnalyzeButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        ReportPanel.Children.Clear();
        ReportHeadline.Text = "Analyzing…";
        _cancel = new CancellationTokenSource();
        try
        {
            // Run the (potentially slow) scan off the UI thread.
            var scanTask = Task.Run(() => _service.Scan(_settings.ExcludedPaths), _cancel.Token);
            var items = await scanTask.WaitAsync(_cancel.Token);

            var enabledItems = items
                .Where(i => enabled.Any(c => c.Id == i.Category.Id))
                .Where(i => i.Bytes > 0)
                .OrderByDescending(i => i.Bytes)
                .ToArray();
            var totalBytes = enabledItems.Sum(i => i.Bytes);
            var registryEntries = enabledItems
                .Where(i => i.Category.Kind == CleanupKind.RegistryValues)
                .Sum(i => i.Bytes);

            _lastPreview = _service.BuildPreview(enabled, _settings.ExcludedPaths);

            foreach (var item in enabledItems)
                ReportPanel.Children.Add(BuildReportRow(item.Category.Name, item.Category.Group, item));

            ReportHeadline.Text = "Analysis complete.";
            StatusText.Text = registryEntries > 0
                ? $"{FormatBytes(totalBytes)} can be removed (incl. {registryEntries:N0} history entries) across {enabledItems.Length} item(s)."
                : $"{FormatBytes(totalBytes)} can be removed across {enabledItems.Length} item(s).";
            if (enabledItems.Length == 0)
            {
                StatusText.Text = "Nothing to clean — the selected items are already clear.";
            }
        }
        catch (OperationCanceledException) { ReportHeadline.Text = "Analysis cancelled."; StatusText.Text = ""; }
        catch (Exception ex) { ReportHeadline.Text = "Analysis failed."; StatusText.Text = ex.Message; }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private static StackPanel BuildReportRow(string title, string group, CleanupItem item)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 26 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\xE8A5", // Document
            FontSize = 13,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{group} · {title}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        var isHistory = item.Category.Kind == CleanupKind.RegistryValues;
        row.Children.Add(new TextBlock
        {
            Text = isHistory ? $"{item.Bytes:N0} entries" : FormatBytes(item.Bytes),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        return row;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCategories();
        if (enabled.Length == 0) { StatusText.Text = "No items are enabled. Select at least one item to clean."; return; }

        var preview = _lastPreview ?? _service.BuildPreview(enabled, _settings.ExcludedPaths);
        if (preview.TotalItems == 0) { StatusText.Text = "Run Analyze first — there is nothing to clean."; return; }

        var review = enabled.Where(c => c.Risk != CleanupRisk.Safe).ToArray();
        if (!await ConfirmPreviewAsync(preview, review)) { StatusText.Text = "Cleanup was not confirmed."; return; }

        AnalyzeButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        ReportPanel.Children.Clear();
        ReportHeadline.Text = "Cleaning…";
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

            ReportHeadline.Text = "Cleaning complete.";
            _lastPreview = null;
            StatusText.Text =
                $"{result.Result.ItemsRemoved:N0} items removed, {FormatBytes(result.Result.BytesRecovered)} recovered, {result.Skipped.Count:N0} skipped.";

            foreach (var category in enabled)
                ReportPanel.Children.Add(BuildResultRow(category, result));
        }
        catch (OperationCanceledException) { ReportHeadline.Text = "Cleaning cancelled."; }
        catch (Exception ex) { ReportHeadline.Text = "Cleaning failed."; StatusText.Text = ex.Message; }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cancel?.Dispose();
            _cancel = null;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static StackPanel BuildResultRow(CleanupCategory category, CleanupReport result)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 26 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\xE73E", // CheckMark
            FontSize = 13,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{category.Group} · {category.Name}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        var issues = result.Skipped.Where(s => s.Path == category.Path).Count();
        if (issues > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"{issues} skipped",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
            });
        }
        return row;
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
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x53, 0x63, 0x5B))
            });
        }
        if (preview.Items.Count < preview.TotalItems)
            panel.Children.Add(new TextBlock
            {
                Text = $"…and {preview.TotalItems - preview.Items.Count:N0} more items will be cleaned.",
                FontSize = 12,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
        if (review.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"Warning: {string.Join(", ", review.Select(c => c.Name))} can remove data permanently.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
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

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
