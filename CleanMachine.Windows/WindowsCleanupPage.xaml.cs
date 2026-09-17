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
        // Load settings/UI, then analyze automatically when the page is opened.
        Loaded += async (_, _) => { await LoadAsync(); Analyze_Click(this, new RoutedEventArgs()); };
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        BuildCategoryList();
    }

    /// <summary>Builds the left-hand category list from the latest analysis: only
    /// categories that have something to clean are shown, unless Show All is ticked
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
                // Windows Update Cleanup (component store) can't be pre-measured without a
                // slow, elevated DISM analysis, so it is always listed and selectable
                // rather than gated on a scanned byte count like the file categories.
                .Where(c => showClean || c.Kind == CleanupKind.ComponentStore || (bytesById.TryGetValue(c.Id, out var b) && b > 0))
                .ToList();
            if (categories.Count == 0) continue;

            CategoryPanel.Children.Add(GroupHeader(group.Key));
            foreach (var category in categories)
            {
                var bytes = bytesById.TryGetValue(category.Id, out var b) ? b : 0;
                var isComponentStore = category.Kind == CleanupKind.ComponentStore;
                var hasData = bytes > 0;
                var selectable = hasData || isComponentStore;
                var size = isComponentStore
                    ? "Admin"
                    : category.Kind == CleanupKind.RegistryValues
                        ? (hasData ? $"{bytes:N0} entries" : "Clean")
                        : (hasData ? FormatBytes(bytes) : "Clean");
                var box = new CheckBox
                {
                    Content = $"{category.Name}  -  {size}",
                    IsChecked = WindowsCleanupService.IsEnabled(category, _settings),
                    Tag = category,
                    MinHeight = 30,
                    IsEnabled = selectable,              // empty file categories are shown but not selectable
                    Opacity = selectable ? 1.0 : 0.5
                };
                if (isComponentStore)
                    ToolTipService.SetToolTip(box, "Removes superseded Windows Update components from the component store (WinSxS). Requires administrator approval, can take several minutes, and cannot be undone.");
                box.Checked += (_, _) => SetEnabled(category, true);
                box.Unchecked += (_, _) => SetEnabled(category, false);
                CategoryPanel.Children.Add(box);
                shown++;
            }
        }

        if (shown == 0)
            CategoryPanel.Children.Add(Hint("Everything is clean. Tick Show All to see all categories."));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => BuildCategoryList();

    /// <summary>Ticks or clears every selectable category currently shown; empty
    /// categories are disabled and left untouched.</summary>
    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var value = SelectAllCheck.IsChecked == true;
        foreach (var box in CategoryPanel.Children.OfType<CheckBox>())
            if (box.IsEnabled)
                box.IsChecked = value;
    }

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
        Progress.IsIndeterminate = true;
        ReportPanel.Children.Clear();
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailHeadline.Text = "Scanning...";
        DetailSubHeadline.Text = "Step 1 of 2: Measuring the selected cleanup categories. Nothing is being removed.";
        _cancel = new CancellationTokenSource();
        try
        {
            // Run the (potentially slow) scan and preview enumeration off the UI
            // thread; the task already honours the cancel token, so no WaitAsync
            // wrapper is needed.
            var items = await Task.Run(() => _service.Scan(_settings.ExcludedPaths), _cancel.Token);

            var enabledItems = items
                .Where(i => enabled.Any(c => c.Id == i.Category.Id))
                .Where(i => i.Bytes > 0)
                .OrderByDescending(i => i.Bytes)
                .ToArray();
            var totalBytes = enabledItems.Sum(i => i.Bytes);
            var registryEntries = enabledItems
                .Where(i => i.Category.Kind == CleanupKind.RegistryValues)
                .Sum(i => i.Bytes);

            _lastPreview = await Task.Run(() => _service.BuildPreview(enabled, _settings.ExcludedPaths), _cancel.Token);
            _lastScan = items;
            BuildCategoryList();

            RenderSummary(enabledItems, totalBytes, registryEntries);
        }
        catch (OperationCanceledException) { DetailHeadline.Text = "Scan cancelled"; DetailSubHeadline.Text = "No cleanup was performed."; StatusText.Text = "Scan cancelled."; }
        catch (Exception ex) { DetailHeadline.Text = "Scan failed"; DetailSubHeadline.Text = "No cleanup was performed."; StatusText.Text = ex.Message; }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private Border BuildReportRow(CleanupCategory category, string group, CleanupItem item)
    {
        var isFile = category.Kind == CleanupKind.Files;
        var isHistory = category.Kind == CleanupKind.RegistryValues;

        // Framed card matching the Application Cleanup / Browser Cleaner right panels:
        // a two-line body on the left, a size/entries stack on the right.
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Spacing = 1 };
        label.Children.Add(new TextBlock
        {
            Text = category.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        label.Children.Add(new TextBlock
        {
            Text = category.Description,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
        });

        // File categories drill into their files on click (transparent button gives the
        // hover/press feedback); history categories have no file list, so they stay static.
        if (isFile)
        {
            var body = new Button
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8),
                Content = label
            };
            body.Click += (_, _) => OnReportRowClicked(category);
            ToolTipService.SetToolTip(body, category.Path ?? category.Description);
            Grid.SetColumn(body, 0);
            row.Children.Add(body);
        }
        else
        {
            label.Margin = new Thickness(10, 8, 10, 8);
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
        }

        var side = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        side.Children.Add(new TextBlock
        {
            Text = isHistory ? $"{item.Bytes:N0}" : FormatBytes(item.Bytes),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
        });
        side.Children.Add(new TextBlock
        {
            Text = isHistory ? "entries" : group,
            FontSize = 10,
            TextAlignment = TextAlignment.Right,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
        });
        Grid.SetColumn(side, 1);
        row.Children.Add(side);

        return new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Tag = category,
            Child = row
        };
    }

    /// <summary>A framed file row for the drill-down, matching the other pages' file
    /// lists: file name on the left, size on the right, full path on hover.</summary>
    private static Border FileCard(string path, long bytes)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(name, path);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);
        var size = new TextBlock
        {
            Text = FormatBytes(bytes),
            FontSize = 10,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);
        return new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Child = grid
        };
    }

    private async void OnReportRowClicked(CleanupCategory category)
    {
        ReportPanel.Children.Clear();
        DetailBackButton.Visibility = Visibility.Visible;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = category.Group.ToUpperInvariant();
        DetailHeadline.Text = category.Name;
        DetailSubHeadline.Text = category.Path ?? category.Description;
        SetChipLabels("FILES", "ITEMS");
        StatusText.Text = "Loading files...";
        Progress.Visibility = Visibility.Visible;

        try
        {
            var files = await Task.Run(() => _service.ScanFiles(category, _settings.ExcludedPaths));

            SetChips(FormatBytes(files.Sum(f => f.Bytes)), files.Count.ToString("N0"), "1");
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
                ReportPanel.Children.Add(FileCard(file.Path, file.Bytes));

            if (files.Count > 300)
            {
                ReportPanel.Children.Add(new TextBlock
                {
                    Text = $"...and {files.Count - 300:N0} more file(s)",
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
        var registryEntries = enabledItems.Where(i => i.Category.Kind == CleanupKind.RegistryValues).Sum(i => i.Bytes);
        RenderSummary(enabledItems, totalBytes, registryEntries);
    }

    /// <summary>Fills the right card with the analysis summary - header, chips, and
    /// one clickable row per enabled category that has data.</summary>
    private void RenderSummary(IReadOnlyList<CleanupItem> enabledItems, long totalBytes, long registryEntries)
    {
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailHeadline.Text = "Analysis complete";
        DetailSubHeadline.Text = enabledItems.Count == 0
            ? "The selected categories are already clear."
            : "Click a category to preview its files.";
        // File counts are only known once a category is opened, so at the summary the
        // middle chip shows item count and the right chip shows history entries.
        SetChipLabels("ITEMS", "HISTORY");
        SetChips(FormatBytes(totalBytes), enabledItems.Count.ToString(),
            registryEntries > 0 ? $"{registryEntries:N0}" : "0");

        StatusText.Text = enabledItems.Count == 0
            ? "Nothing to clean - the selected items are already clear."
            : registryEntries > 0
                ? $"{FormatBytes(totalBytes)} can be removed (incl. {registryEntries:N0} history entries) across {enabledItems.Count} item(s)."
                : $"{FormatBytes(totalBytes)} can be removed across {enabledItems.Count} item(s).";

        ReportPanel.Children.Clear();
        foreach (var item in enabledItems)
            ReportPanel.Children.Add(BuildReportRow(item.Category, item.Category.Group, item));
    }

    /// <summary>Updates the summary chip values; a null value shows an em dash.</summary>
    private void SetChips(string? size, string? mid, string? right)
    {
        ChipSizeValue.Text = size ?? "-";
        ChipFilesValue.Text = mid ?? "-";
        ChipItemsValue.Text = right ?? "-";
    }

    /// <summary>Relabels the middle and right chips; the size chip is always "TO CLEAN".</summary>
    private void SetChipLabels(string mid, string right)
    {
        ChipMidLabel.Text = mid;
        ChipRightLabel.Text = right;
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e) => RestoreSummary();

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EnabledCategories();
        if (enabled.Length == 0) { StatusText.Text = "No items are enabled. Select at least one item to clean."; return; }

        // _lastPreview is normally filled by Analyze; the fallback build enumerates
        // every category's files, so keep it off the UI thread too.
        var preview = _lastPreview ?? await Task.Run(() => _service.BuildPreview(enabled, _settings.ExcludedPaths));
        if (preview.TotalItems == 0) { StatusText.Text = "Run Analyze first - there is nothing to clean."; return; }

        var review = enabled.Where(c => c.Risk != CleanupRisk.Safe).ToArray();
        if (!await ConfirmPreviewAsync(preview, review)) { StatusText.Text = "Cleanup was not confirmed."; return; }

        AnalyzeButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ReportPanel.Children.Clear();
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailHeadline.Text = "Cleaning...";
        DetailSubHeadline.Text = "Step 2 of 2: Removing the selected items. This can take a while for large folders or DISM.";
        StatusText.Text = "Cleaning in progress: preparing the selected items...";
        _cancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CleanupProgress>(p =>
            {
                Progress.IsIndeterminate = p.Total == 0;
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                StatusText.Text = p.Total == 0
                    ? $"{p.Phase}..."
                    : $"Cleaning {p.Phase}: {p.Completed}/{p.Total}";
            });
            var useSecureDelete = SecureDeleteCheck.IsChecked == true;
            var secureDeleteOpts = useSecureDelete ? new SecureDeleteOptions(_settings.SecureDeleteMethod, _settings.CustomWipePasses) : null;
            var result = await _service.CleanSelectedAsync(
                enabled,
                new WindowsCleanupOptions(ConfirmReviewCategories: true, ExcludedPaths: _settings.ExcludedPaths, SecureDelete: useSecureDelete, SecureDeleteOptions: secureDeleteOpts),
                progress,
                _cancel.Token);

            DetailHeadline.Text = "Cleaning complete";
            DetailSubHeadline.Text = "The cleanup finished. Review the removed and skipped totals below.";
            await RecordManualCleanupAsync(result);
            _lastPreview = null;
            StatusText.Text =
                $"{result.Result.ItemsRemoved:N0} items removed, {FormatBytes(result.Result.BytesRecovered)} recovered, {result.Skipped.Count:N0} skipped.";
            SetChipLabels("REMOVED", "SKIPPED");
            SetChips(FormatBytes(result.Result.BytesRecovered), result.Result.ItemsRemoved.ToString("N0"), result.Skipped.Count.ToString("N0"));

            foreach (var category in enabled)
                ReportPanel.Children.Add(BuildResultRow(category, result));

            // Re-measure so the left list and cached sizes reflect what was cleaned
            // (cleaned categories drop to zero and leave the list unless Show All is on).
            try
            {
                _lastScan = await Task.Run(() => _service.Scan(_settings.ExcludedPaths), CancellationToken.None);
                BuildCategoryList();
            }
            catch { /* refresh is best-effort; the completion report still stands */ }
        }
        catch (OperationCanceledException) { DetailHeadline.Text = "Cleaning cancelled"; DetailSubHeadline.Text = "Some items may not have been changed."; StatusText.Text = "Cleaning cancelled."; }
        catch (Exception ex) { DetailHeadline.Text = "Cleaning failed"; DetailSubHeadline.Text = "No further cleanup is running."; StatusText.Text = ex.Message; }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cancel?.Dispose();
            _cancel = null;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task RecordManualCleanupAsync(CleanupReport report)
    {
        await new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered);
        try
        {
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                "Windows Cleanup",
                $"Manual clean - removed {report.Result.ItemsRemoved:N0} item(s), {FormatBytes(report.Result.BytesRecovered)} recovered, {report.Skipped.Count:N0} skipped.",
                ActivityStore.BreakdownLines(report.Breakdown)));
        }
        catch
        {
            // Activity history is diagnostic; a completed cleanup must not be
            // reported as failed if the history file cannot be written.
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
            Text = $"{category.Group} - {category.Name}",
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
                Text = $"...and {preview.TotalItems - preview.Items.Count:N0} more items will be cleaned.",
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

    /// <summary>Shared byte formatter; the canonical implementation lives in
    /// <see cref="AppNotifications.FormatBytes"/> and this alias keeps the many
    /// page call sites unchanged.</summary>
    internal static string FormatBytes(long bytes) => AppNotifications.FormatBytes(bytes);

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
