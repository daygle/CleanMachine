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

    public AppCleanupPage()
    {
        InitializeComponent();
        // Load settings, then scan automatically when the page is opened.
        Loaded += async (_, _) => { _settings = await AppSettings.LoadAsync(); Scan_Click(this, new RoutedEventArgs()); };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Detecting installed applications and measuring temp files...";
        AppListPanel.Children.Clear();
        _itemBoxes.Clear();
        try
        {
            _lastScans = await _service.ScanAllAsync();
            var installed = _lastScans.Where(s => s.Installed).ToList();

            if (installed.Count == 0)
            {
                StatusText.Text = "No supported applications were detected.";
                return;
            }

            RenderAppList(installed);

            var totalItems = installed.Sum(s => s.Items.Count);
            var totalBytes = installed.Sum(s => s.Items.Sum(i => i.Bytes));
            StatusText.Text = $"{installed.Count} app(s) detected with {totalItems} cleanable item(s) ({WindowsCleanupPage.FormatBytes(totalBytes)}).";
            CleanButton.IsEnabled = totalItems > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderAppList(IReadOnlyList<AppScan> installed)
    {
        var showClean = ShowCleanCheck.IsChecked == true;
        var visible = showClean ? installed : installed.Where(s => s.Items.Count > 0).ToList();

        AppListPanel.Children.Clear();
        _itemBoxes.Clear();
        DetailHeadline.Text = "Select an application";
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
            OnItemClicked(firstWithItems, 0);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastScans.Count == 0) return;
        var installed = _lastScans.Where(s => s.Installed).ToList();
        RenderAppList(installed);
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
            Text = totalBytes > 0 ? WindowsCleanupPage.FormatBytes(totalBytes) : "Clean",
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
            detailsButton.Click += (_, _) => OnItemClicked(scan, itemIndex);
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
        // Expanding an app shows its first item's files in the detail card, so the
        // card fills in without the user having to click each item.
        expander.Expanding += (_, _) => OnItemClicked(scan, 0);
        return expander;
    }

    private void OnItemClicked(AppScan scan, int itemIndex)
    {
        var item = scan.Items.ElementAtOrDefault(itemIndex);
        if (item is null) return;

        // Avoid a redundant "Activity History - Activity history" when the app has a
        // single item whose description just restates the app name.
        DetailHeadline.Text = string.Equals(scan.Name, item.Description, StringComparison.OrdinalIgnoreCase)
            ? scan.Name
            : $"{scan.Name} - {item.Description}";
        StatusText.Text = $"{item.FileCount:N0} file(s), {WindowsCleanupPage.FormatBytes(item.Bytes)}";
        DetailPanel.Children.Clear();

        try
        {
            if (Directory.Exists(item.FullPath))
            {
                var files = Directory.EnumerateFiles(item.FullPath, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .ToList();

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    var relPath = file[item.FullPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
                    row.Children.Add(new TextBlock
                    {
                        Text = relPath,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 260
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = WindowsCleanupPage.FormatBytes(info.Length),
                        FontSize = 10,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    DetailPanel.Children.Add(row);
                }

                if (item.FileCount > 200)
                {
                    DetailPanel.Children.Add(new TextBlock
                    {
                        Text = $"…and {item.FileCount - 200:N0} more file(s)",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                }
            }
            else if (File.Exists(item.FullPath))
            {
                var info = new FileInfo(item.FullPath);
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = $"{item.FullPath}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
                    TextWrapping = TextWrapping.Wrap
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

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _itemBoxes
            .Where(x => x.Box.IsChecked == true)
            .Select(x => (x.AppId, x.ItemIndex))
            .ToArray();
        if (selected.Length == 0) { StatusText.Text = "Nothing is ticked. Tick at least one item to clean."; return; }

        var totalBytes = _itemBoxes
            .Where(x => x.Box.IsChecked == true)
            .Sum(x =>
            {
                var scan = _lastScans.FirstOrDefault(s => s.Id == x.AppId);
                var item = scan?.Items.ElementAtOrDefault(x.ItemIndex);
                return item?.Bytes ?? 0;
            });

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
        StatusText.Text = "Cleaning...";
        try
        {
            var secureDelete = SecureDeleteCheck.IsChecked == true
                ? new SecureDeleteOptions(_settings.SecureDeleteMethod, _settings.CustomWipePasses)
                : null;
            var report = await _service.CleanAsync(selected, secureDelete);
            StatusText.Text = $"Complete: {report.Result.ItemsRemoved:N0} file(s) removed, " +
                              $"{WindowsCleanupPage.FormatBytes(report.Result.BytesRecovered)} recovered, " +
                              $"{report.Skipped.Count:N0} skipped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            CleanButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
