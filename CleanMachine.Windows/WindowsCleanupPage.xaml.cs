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
    private IReadOnlyList<CleanupItem> _lastScan = [];

    public WindowsCleanupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        BuildCategoryList();
    }

    /// <summary>Builds the left-hand category list from the latest analysis: only
    /// categories that have something to clean are shown, unless Show Clean is ticked
    /// (empty categories are then listed, greyed out and not selectable). Before the
    /// first Analyze the list is a prompt. Each enabled checkbox reflects and saves
    /// whether that category is included in cleaning.</summary>
    private void BuildCategoryList()
    {
        CategoryPanel.Children.Clear();

        if (_lastScan.Count == 0)
        {
            CategoryPanel.Children.Add(Hint("Click Analyze to scan and list cleanable items."));
            return;
        }

        var showClean = ShowCleanCheck.IsChecked == true;
        var bytesById = _lastScan.ToDictionary(i => i.Category.Id, i => i.Bytes);
        var shown = 0;

        foreach (var group in WindowsCleanupService.Catalog
                     .GroupBy(c => c.Group)
                     .OrderBy(g => GroupIndex(g.Key)))
        {
            var categories = group.OrderBy(c => c.Name)
                .Where(c => showClean || (bytesById.TryGetValue(c.Id, out var b) && b > 0))
                .ToList();
            if (categories.Count == 0) continue;

            CategoryPanel.Children.Add(GroupHeader(group.Key));
            foreach (var category in categories)
            {
                var bytes = bytesById.TryGetValue(category.Id, out var b) ? b : 0;
                var hasData = bytes > 0;
                var size = category.Kind == CleanupKind.RegistryValues
                    ? (hasData ? $"{bytes:N0} entries" : "Clean")
                    : (hasData ? FormatBytes(bytes) : "Clean");
                var box = new CheckBox
                {
                    Content = $"{category.Name}  ·  {size}",
                    IsChecked = WindowsCleanupService.IsEnabled(category, _settings),
                    Tag = category,
                    MinHeight = 30,
                    IsEnabled = hasData,                 // empty categories are shown but not selectable
                    Opacity = hasData ? 1.0 : 0.5
                };
                box.Checked += (_, _) => SetEnabled(category, true);
                box.Unchecked += (_, _) => SetEnabled(category, false);
                CategoryPanel.Children.Add(box);
                shown++;
            }
        }

        if (shown == 0)
            CategoryPanel.Children.Add(Hint("Everything is clean. Tick Show Clean to see all categories."));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => BuildCategoryList();

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)),
        Margin = new Thickness(0, 12, 0, 2)
    };

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

    private CleanupCategory[] EnabledCategories() =>
        WindowsCleanupService.Catalog.Where(c => WindowsCleanupService.IsEnabled(c, _settings)).ToArray();

    /// <summary>Analysis: measure every enabled category and render a CCleaner-style
    /// report (one row per category with its size and item count). Nothing is removed.</summary>
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        // Analyze always scans the whole catalog (the left list is populated from the
        // result), so there is nothing to pre-select before scanning.
        var enabled = EnabledCategories();

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
            _lastScan = items;
            BuildCategoryList();

            foreach (var item in enabledItems)
                ReportPanel.Children.Add(BuildReportRow(item.Category, item.Category.Group, item));

            ReportHeadline.Text = "Analysis complete.";
            StatusText.Text = registryEntries > 0
                ? $"{FormatBytes(totalBytes)} can be removed (incl. {registryEntries:N0} history entries) across {enabledItems.Length} item(s)."
                : $"{FormatBytes(totalBytes)} can be removed across {enabledItems.Length} item(s).";
            if (enabledItems.Length == 0)
            {
                StatusText.Text = "Nothing to clean - the selected items are already clear.";
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

    private StackPanel BuildReportRow(CleanupCategory category, string group, CleanupItem item)
    {
        var isFile = category.Kind == CleanupKind.Files;
        var isHistory = category.Kind == CleanupKind.RegistryValues;

        var border = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Tag = category,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
        if (isFile)
        {
            border.PointerEntered += (_, _) => border.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xF3, 0xF8, 0xF5));
            border.PointerExited += (_, _) => border.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            border.PointerPressed += (_, _) => OnReportRowClicked(category);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 26 };
        row.Children.Add(new FontIcon
        {
            Glyph = isFile ? "\xE8A5" : "\xEA18", // Document : Database
            FontSize = 13,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{group} · {category.Name}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        row.Children.Add(new TextBlock
        {
            Text = isHistory ? $"{item.Bytes:N0} entries" : FormatBytes(item.Bytes),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        if (isFile)
        {
            row.Children.Add(new TextBlock
            {
                Text = "\uE974", // RightArrow
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x9A, 0xA6, 0xA1))
            });
        }

        border.Child = row;
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(border);
        return panel;
    }

    private async void OnReportRowClicked(CleanupCategory category)
    {
        ReportPanel.Children.Clear();
        ReportHeadline.Text = $"{category.Group} · {category.Name}";
        StatusText.Text = "Loading files...";
        Progress.Visibility = Visibility.Visible;

        try
        {
            var files = await Task.Run(() => _service.ScanFiles(category, _settings.ExcludedPaths));

            // Back button
            var backRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
            var backBtn = new Button
            {
                Content = "\uE72B  Back to summary", // BackIcon
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0)
            };
            backBtn.Click += (_, _) => RestoreSummary();
            backRow.Children.Add(backBtn);
            ReportPanel.Children.Add(backRow);

            StatusText.Text = $"{files.Count:N0} file(s), {FormatBytes(files.Sum(f => f.Bytes))}";

            if (files.Count == 0)
            {
                ReportPanel.Children.Add(new TextBlock
                {
                    Text = "No files found for this category.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                    Margin = new Thickness(0, 12, 0, 0)
                });
                return;
            }

            var display = files.Take(300).ToList();
            foreach (var file in display)
            {
                var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
                fileRow.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(file.Path),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320,
                    VerticalAlignment = VerticalAlignment.Center
                });
                fileRow.Children.Add(new TextBlock
                {
                    Text = FormatBytes(file.Bytes),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                ToolTipService.SetToolTip(fileRow, file.Path);
                ReportPanel.Children.Add(fileRow);
            }

            if (files.Count > 300)
            {
                ReportPanel.Children.Add(new TextBlock
                {
                    Text = $"…and {files.Count - 300:N0} more file(s)",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to list files: {ex.Message}";
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void RestoreSummary()
    {
        if (_lastScan.Count == 0) return;
        var enabled = EnabledCategories();
        var enabledItems = _lastScan
            .Where(i => enabled.Any(c => c.Id == i.Category.Id))
            .Where(i => i.Bytes > 0)
            .OrderByDescending(i => i.Bytes)
            .ToArray();
        var totalBytes = enabledItems.Sum(i => i.Bytes);

        ReportPanel.Children.Clear();
        ReportHeadline.Text = "Analysis complete.";
        StatusText.Text = $"{FormatBytes(totalBytes)} can be removed across {enabledItems.Length} item(s).";

        foreach (var item in enabledItems)
            ReportPanel.Children.Add(BuildReportRow(item.Category, item.Category.Group, item));
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCategories();
        if (enabled.Length == 0) { StatusText.Text = "No items are enabled. Select at least one item to clean."; return; }

        var preview = _lastPreview ?? _service.BuildPreview(enabled, _settings.ExcludedPaths);
        if (preview.TotalItems == 0) { StatusText.Text = "Run Analyze first - there is nothing to clean."; return; }

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
            var useSecureDelete = SecureDeleteCheck.IsChecked == true;
            var secureDeleteOpts = useSecureDelete ? new SecureDeleteOptions(_settings.SecureDeleteMethod, _settings.CustomWipePasses) : null;
            var result = await _service.CleanSelectedAsync(
                enabled,
                new WindowsCleanupOptions(ConfirmReviewCategories: true, ExcludedPaths: _settings.ExcludedPaths, SecureDelete: useSecureDelete, SecureDeleteOptions: secureDeleteOpts),
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
                Text = $"{item.Category} - {item.Description}{size}",
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
