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
        Loaded += async (_, _) =>
        {
            _settings = await AppSettings.LoadAsync();
            ShowCleanAppsCheck.IsChecked = _settings.ShowCleanApps;
        };
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
            var withItems = installed.Where(s => s.Items.Count > 0).ToList();
            var cleanApps = _settings.ShowCleanApps ? installed.Where(s => s.Items.Count == 0).ToList() : [];

            if (installed.Count == 0)
            {
                StatusText.Text = "No supported applications were detected.";
                return;
            }

            // When every detected app is clean and clean apps are hidden, say so
            // instead of rendering an empty list.
            if (withItems.Count == 0 && cleanApps.Count == 0)
            {
                StatusText.Text = $"{installed.Count} app(s) detected, all clean - nothing to clean.";
                return;
            }

            RenderList(withItems, cleanApps);
            StatusText.Text = BuildSummaryText(installed, withItems, cleanApps);
            CleanButton.IsEnabled = withItems.Sum(s => s.Items.Count) > 0;
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

    /// <summary>Persists the "Show clean apps" preference and re-renders the list
    /// from the last scan without re-measuring files on disk.</summary>
    private async void ShowCleanApps_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowCleanAppsCheck.IsChecked == true;
        if (_settings.ShowCleanApps == show) return; // initial IsChecked assignment, not a user change
        _settings.ShowCleanApps = show;
        await _settings.SaveAsync();

        // Re-render from the cached scan so no disk re-measure is needed. If no
        // scan has run yet, the next Scan will honor the setting.
        if (_lastScans.Count == 0) return;
        AppListPanel.Children.Clear();
        _itemBoxes.Clear();
        var installed = _lastScans.Where(s => s.Installed).ToList();
        var withItems = installed.Where(s => s.Items.Count > 0).ToList();
        var cleanApps = show ? installed.Where(s => s.Items.Count == 0).ToList() : [];
        RenderList(withItems, cleanApps);
        StatusText.Text = BuildSummaryText(installed, withItems, cleanApps);
    }

    private static string BuildSummaryText(
        IReadOnlyList<AppScan> installed, IReadOnlyList<AppScan> withItems, IReadOnlyList<AppScan> cleanApps)
    {
        var totalItems = withItems.Sum(s => s.Items.Count);
        var totalBytes = withItems.Sum(s => s.Items.Sum(i => i.Bytes));
        return withItems.Count == 0
            ? $"{installed.Count} app(s) detected, all clean - nothing to clean."
              + (cleanApps.Count > 0 ? " They are shown greyed out." : string.Empty)
            : $"{withItems.Count} of {installed.Count} app(s) detected have {totalItems} cleanable item(s) ({WindowsCleanupPage.FormatBytes(totalBytes)})."
              + (cleanApps.Count > 0 ? $" {cleanApps.Count} clean app(s) shown greyed out." : " Apps that are already clean are hidden.");
    }

    /// <summary>Renders the grouped app list. Apps with cleanable items are
    /// interactive; clean apps (when shown) are greyed out and non-expandable.</summary>
    private void RenderList(IReadOnlyList<AppScan> withItems, IReadOnlyList<AppScan> cleanApps)
    {
        foreach (var group in withItems.Concat(cleanApps).GroupBy(s => s.Group))
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
            Text = totalBytes > 0 ? WindowsCleanupPage.FormatBytes(totalBytes) : "clean",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = scan.Items.Count > 0,
            IsEnabled = scan.Items.Count > 0,
            // Clean apps render greyed out when they are shown.
            Opacity = scan.Items.Count > 0 ? 1.0 : 0.55,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (scan.Items.Count == 0) return expander;

        var content = new StackPanel { Spacing = 2 };
        for (var i = 0; i < scan.Items.Count; i++)
        {
            var item = scan.Items[i];
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

            var box = new CheckBox
            {
                Content = text,
                IsChecked = true,
                MinHeight = 30,
                Tag = (scan.Id, i)
            };
            ToolTipService.SetToolTip(box, item.FullPath);
            _itemBoxes.Add((scan.Id, i, box));
            content.Children.Add(box);
        }

        expander.Content = content;
        return expander;
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
            _ = new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered);
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
