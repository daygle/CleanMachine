using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class AppCleanupPage : Page
{
    private readonly AppCleanupService _service = new();
    private readonly List<(string AppId, int ItemIndex, CheckBox Box)> _itemBoxes = [];
    private AppSettings _settings = new();
    private IReadOnlyList<AppScan> _lastScans = [];
    // Detail-card navigation: the app whose detail is shown, and the index of the
    // item drilled into (null = the app-level view listing all of its items).
    private AppScan? _detailContext;
    private int? _detailItemIndex;

    public AppCleanupPage()
    {
        InitializeComponent();
        // Load settings, then scan automatically when the page is opened.
        Loaded += async (_, _) => { _settings = await AppSettings.LoadAsync(); Scan_Click(this, new RoutedEventArgs()); };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    /// <summary>Detects apps and measures their temp files, then rebuilds the list and
    /// detail card. <paramref name="statusOverride"/> keeps a caller's message (e.g. a
    /// post-clean summary) instead of the detection summary, so re-scanning after a
    /// clean refreshes the sizes without hiding what was just cleaned.</summary>
    private async Task ScanAsync(string? statusOverride = null)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        SetChipLabels("TO CLEAN", "FILES", "ITEMS");
        DetailHeadline.Text = "Scanning...";
        DetailSubHeadline.Text = "Step 1 of 2: Detecting applications and measuring temporary files.";
        StatusText.Text = "Scanning installed applications and measuring temporary files...";
        AppListPanel.Children.Clear();
        _itemBoxes.Clear();
        try
        {
            _lastScans = await _service.ScanAllAsync();
            var installed = _lastScans.Where(s => s.Installed).ToList();

            if (installed.Count == 0)
            {
                DetailHeadline.Text = statusOverride is null ? "Scan complete" : "Cleaning complete";
                DetailSubHeadline.Text = statusOverride is null
                    ? "No supported applications were detected."
                    : "The cleanup finished. No supported application data remains to show.";
                StatusText.Text = statusOverride ?? "Scan complete: no supported applications were detected.";
                return;
            }

            RenderAppList(installed);

            var totalItems = installed.Sum(s => s.Items.Count);
            var totalBytes = installed.Sum(s => s.Items.Sum(i => i.Bytes));
            DetailHeadline.Text = statusOverride is null ? "Scan complete" : "Cleaning complete";
            DetailSubHeadline.Text = statusOverride is null
                ? $"Found {totalItems:N0} cleanable item(s) across {installed.Count:N0} application(s)."
                : "The cleanup finished. Review the summary below.";
            StatusText.Text = statusOverride
                ?? $"Scan complete: {installed.Count} app(s) detected with {totalItems} cleanable item(s) ({WindowsCleanupPage.FormatBytes(totalBytes)}).";
            CleanButton.IsEnabled = totalItems > 0;
        }
        catch (Exception ex)
        {
            DetailHeadline.Text = "Scan failed";
            DetailSubHeadline.Text = "The application scan could not be completed.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderAppList(IReadOnlyList<AppScan> installed)
    {
        var showClean = ShowCleanCheck.IsChecked == true;
        var visible = showClean ? installed : installed.Where(s => s.Items.Count > 0).ToList();

        AppListPanel.Children.Clear();
        _itemBoxes.Clear();
        _detailContext = null;
        _detailItemIndex = null;
        DetailBackButton.Visibility = Visibility.Collapsed;
        ShowDetailPlaceholder();
        StatusText.Text = visible.Count == 0
            ? "All apps are clean. Check 'Show All' to see them."
            : $"{visible.Count} app(s) with cleanable files.";
        DetailPanel.Children.Clear();

        foreach (var group in visible.GroupBy(s => s.Group))
        {
            AppListPanel.Children.Add(new TextBlock
            {
                Text = $"{group.Key.ToUpperInvariant()} ({group.Count()})",
                FontSize = 11,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)),
                Margin = new Thickness(0, 10, 0, 4)
            });

            foreach (var scan in group)
                AppListPanel.Children.Add(BuildAppCard(scan));
        }

        // Populate the detail card straight away with the first app that has items,
        // so the right side is never a blank "Select an application" until clicked.
        var firstWithItems = visible.FirstOrDefault(s => s.Items.Count > 0);
        if (firstWithItems is not null)
            ShowAppDetail(firstWithItems);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastScans.Count == 0) return;
        var installed = _lastScans.Where(s => s.Installed).ToList();
        RenderAppList(installed);
    }

    /// <summary>Ticks or clears every item box currently shown (all apps, all items).</summary>
    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var value = SelectAllCheck.IsChecked == true;
        foreach (var entry in _itemBoxes)
            entry.Box.IsChecked = value;
    }

    private Expander BuildAppCard(AppScan scan)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon
        {
            Glyph = "\uE7B8",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
        });
        header.Children.Add(new TextBlock
        {
            Text = scan.Name,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        var totalBytes = scan.Items.Sum(i => i.Bytes);
        header.Children.Add(new TextBlock
        {
            // Show the real total even when it is zero bytes - an app can have
            // items that exist yet hold only empty files, and labelling that
            // "Clean" while the item rows still list files is contradictory.
            Text = scan.Items.Count > 0 ? WindowsCleanupPage.FormatBytes(totalBytes) : "Clean",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = scan.Items.Count > 0,
            IsEnabled = scan.Items.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (scan.Items.Count == 0) return expander;

        var content = new StackPanel { Spacing = 2 };
        for (var i = 0; i < scan.Items.Count; i++)
        {
            var item = scan.Items[i];
            var itemIndex = i;
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 12,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{WindowsCleanupPage.FormatBytes(item.Bytes)} - {item.FileCount:N0} file(s)",
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

            // The checkbox only decides whether the item is cleaned. Viewing its
            // files is a separate action on the label button, so opening the detail
            // card no longer toggles the tick (previously the two were the same click).
            var box = new CheckBox
            {
                IsChecked = true,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = (scan.Id, i)
            };
            _itemBoxes.Add((scan.Id, i, box));

            var detailsButton = new Button
            {
                Content = text,
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            detailsButton.Click += (_, _) => ShowItemDetail(scan, itemIndex);
            ToolTipService.SetToolTip(detailsButton, item.FullPath);

            var row = new Grid { MinHeight = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(detailsButton, 1);
            row.Children.Add(box);
            row.Children.Add(detailsButton);
            content.Children.Add(row);
        }

        expander.Content = content;
        // Expanding an app shows the app-level detail (all of its items); clicking
        // one item drills into that item's files. Expanding is not a drill-down,
        // so it does not force the first item's files on the user.
        expander.Expanding += (_, _) => ShowAppDetail(scan);
        return expander;
    }

    /// <summary>App-level detail: a summary header with live selection totals,
    /// then one framed card per cleanable item. The checkbox mirrors the app
    /// card's box in _itemBoxes, so the card stays the single source of truth.</summary>
    private void ShowAppDetail(AppScan scan)
    {
        _detailContext = scan;
        _detailItemIndex = null;
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = scan.Group.ToUpperInvariant();
        DetailHeadline.Text = scan.Name;
        DetailSubHeadline.Text = "Click an item card to preview its files.";
        DetailPanel.Children.Clear();

        if (scan.Items.Count == 0)
        {
            SetChips(null, null, null);
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "Nothing to clean for this app.",
                FontSize = 12,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            return;
        }

        SetChipLabels("TO CLEAN", "FILES", "ITEMS");
        SetChips(WindowsCleanupPage.FormatBytes(scan.Items.Sum(i => i.Bytes)),
            scan.Items.Sum(i => i.FileCount).ToString("N0"),
            scan.Items.Count.ToString());

        foreach (var (item, index) in scan.Items.Select((item, index) => (item, index)))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Mirrors the app card's checkbox for the same item: the card's box is
            // the single source of truth in _itemBoxes, and toggling either one
            // toggles both. Rebuilding the detail panel therefore cannot leave
            // stale or duplicate entries behind.
            var cardBox = _itemBoxes.FirstOrDefault(x => x.AppId == scan.Id && x.ItemIndex == index).Box;
            var box = new CheckBox
            {
                IsChecked = cardBox?.IsChecked == true,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            box.Checked += (_, _) => { if (cardBox is not null) cardBox.IsChecked = true; };
            box.Unchecked += (_, _) => { if (cardBox is not null) cardBox.IsChecked = false; };
            Grid.SetColumn(box, 0);
            row.Children.Add(box);

            // Same transparent-button pattern as the app card rows: the Button
            // contributes hover/pressed feedback without extra styling here.
            var body = new Button
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8)
            };
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            label.Children.Add(new TextBlock
            {
                Text = item.FullPath,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            body.Content = label;
            body.Click += (_, _) => ShowItemDetail(scan, index);
            ToolTipService.SetToolTip(body, item.FullPath);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            var side = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            side.Children.Add(new TextBlock
            {
                Text = WindowsCleanupPage.FormatBytes(item.Bytes),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
            });
            side.Children.Add(new TextBlock
            {
                Text = $"{item.FileCount:N0} file(s)",
                FontSize = 10,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            Grid.SetColumn(side, 2);
            row.Children.Add(side);

            DetailPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                Child = row
            });
        }
    }

    /// <summary>Item-level detail: a location banner plus the files of the item
    /// drilled into. The back button returns to the app-level view.</summary>
    private void ShowItemDetail(AppScan scan, int itemIndex)
    {
        var item = scan.Items.ElementAtOrDefault(itemIndex);
        if (item is null) return;

        _detailContext = scan;
        _detailItemIndex = itemIndex;
        DetailBackButton.Visibility = Visibility.Visible;
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        // Avoid a redundant "Activity History - Activity history" when the app has a
        // single item whose description just restates the app name.
        DetailHeadline.Text = string.Equals(scan.Name, item.Description, StringComparison.OrdinalIgnoreCase)
            ? scan.Name
            : $"{scan.Name} - {item.Description}";
        DetailSubHeadline.Text = item.FullPath;
        SetChips(WindowsCleanupPage.FormatBytes(item.Bytes), item.FileCount.ToString("N0"), "1");
        DetailPanel.Children.Clear();

        try
        {
            if (Directory.Exists(item.FullPath))
            {
                var files = FileEnumeration.Files(item.FullPath)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .ToList();

                if (files.Count == 0)
                    DetailPanel.Children.Add(BuildDetailPlaceholder("No files in this location right now."));

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    var relPath = file[item.FullPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fileRow = new Grid { ColumnSpacing = 12 };
                    fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var nameText = new TextBlock
                    {
                        Text = relPath,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(nameText, 0);
                    fileRow.Children.Add(nameText);
                    var sizeText = new TextBlock
                    {
                        Text = WindowsCleanupPage.FormatBytes(info.Length),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(sizeText, 1);
                    fileRow.Children.Add(sizeText);
                    DetailPanel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
                        BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5),
                        Child = fileRow
                    });
                }

                if (item.FileCount > 200)
                {
                    DetailPanel.Children.Add(new TextBlock
                    {
                        Text = $"...and {item.FileCount - 200:N0} more file(s)",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                        Margin = new Thickness(2, 6, 0, 0)
                    });
                }
            }
            else if (File.Exists(item.FullPath))
            {
                var info = new FileInfo(item.FullPath);
                DetailPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
                    BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = new TextBlock
                    {
                        Text = item.FullPath,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = WindowsCleanupPage.FormatBytes(info.Length),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
                });
            }
            else
            {
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "Path not found",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
                });
            }
        }
        catch (Exception ex)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = $"Unable to list files: {ex.Message}",
                FontSize = 11,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
            });
        }
    }

    /// <summary>Updates the summary chips (to-clean size, file count, item count).
    /// A null value collapses that chip back to its empty dash.</summary>
    private void SetChipLabels(string size, string files, string items)
    {
        ChipSizeLabel.Text = size;
        ChipFilesLabel.Text = files;
        ChipItemsLabel.Text = items;
    }

    private void SetChips(string? bytes, string? files, string? items)
    {
        ChipSizeValue.Text = bytes ?? "\u2014";
        ChipFilesValue.Text = files ?? "\u2014";
        ChipItemsValue.Text = items ?? "\u2014";
    }

    /// <summary>Framed muted message used when a list has nothing to show, so the
    /// empty case still reads as intentional design rather than a broken panel.</summary>
    private static Border BuildDetailPlaceholder(string message) => new()
    {
        BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Child = new TextBlock
        {
            Text = message,
            FontSize = 11,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        }
    };

    /// <summary>Reset state for the right card before any app is selected.</summary>
    private void ShowDetailPlaceholder()
    {
        DetailHeadline.Text = "Application details";
        DetailSubHeadline.Text = "Pick an application on the left to review what can be freed.";
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        SetChips(null, null, null);
    }

    /// <summary>Back navigation from a drilled-into item to its app's item list.</summary>
    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        if (_detailContext is { } scan)
            ShowAppDetail(scan);
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        // Distinct: the same item can be ticked in both the app card and the
        // detail panel (mirrored checkboxes), and must count once.
        var selected = _itemBoxes
            .Where(x => x.Box.IsChecked == true)
            .Select(x => (x.AppId, x.ItemIndex))
            .Distinct()
            .ToArray();
        if (selected.Length == 0) { StatusText.Text = "Nothing is ticked. Tick at least one item to clean."; return; }

        var totalBytes = selected.Sum(x =>
            _lastScans.FirstOrDefault(s => s.Id == x.AppId)?.Items.ElementAtOrDefault(x.ItemIndex)?.Bytes ?? 0);

        var confirm = new ContentDialog
        {
            Title = "Clean application temp files?",
            Content = $"This will remove {selected.Length} item(s) ({WindowsCleanupPage.FormatBytes(totalBytes)}). Temp files are recreated by apps when needed. Continue?",
            PrimaryButtonText = "Clean",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        DetailHeadline.Text = "Cleaning...";
        DetailSubHeadline.Text = "Step 2 of 2: Removing selected temporary files.";
        StatusText.Text = $"Cleaning in progress: preparing {selected.Length:N0} selected item(s)...";
        try
        {
            var secureDelete = SecureDeleteCheck.IsChecked == true
                ? new SecureDeleteOptions(_settings.SecureDeleteMethod, _settings.CustomWipePasses)
                : null;
            var progress = new Progress<CleanupProgress>(p =>
            {
                Progress.IsIndeterminate = p.Total == 0;
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                StatusText.Text = p.Total == 0
                    ? $"{p.Phase}..."
                    : $"Cleaning in progress: {p.Phase} ({p.Completed:N0}/{p.Total:N0})";
            });
            var report = await _service.CleanAsync(selected, secureDelete, progress: progress, token: default);
            var completion = $"Complete: {report.Result.ItemsRemoved:N0} file(s) removed, " +
                             $"{WindowsCleanupPage.FormatBytes(report.Result.BytesRecovered)} recovered, " +
                             $"{report.Skipped.Count:N0} skipped.";
            DetailHeadline.Text = "Cleaning complete";
            DetailSubHeadline.Text = "The cleanup finished. Refreshing the application list...";
            StatusText.Text = completion;
            // Re-scan so the list and sizes reflect what was just cleaned, keeping the
            // completion message as the status.
            await ScanAsync(completion);
            SetChipLabels("RECOVERED", "REMOVED", "SKIPPED");
            SetChips(WindowsCleanupPage.FormatBytes(report.Result.BytesRecovered),
                report.Result.ItemsRemoved.ToString("N0"), report.Skipped.Count.ToString("N0"));
            DetailHeadline.Text = "Cleaning complete";
            DetailSubHeadline.Text = "The cleanup finished. Review the recovered, removed, and skipped totals.";
            StatusText.Text = completion;
        }
        catch (Exception ex)
        {
            DetailHeadline.Text = "Cleaning failed";
            DetailSubHeadline.Text = "No further cleanup is running.";
            StatusText.Text = ex.Message;
        }
        finally
        {
            CleanButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
