using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class CleanerPage : Page
{
    private readonly BrowserCleanupService _service = new();
    private readonly List<(string BrowserId, string ItemId, bool Destructive, CheckBox Box)> _itemBoxes = [];
    private AppSettings _settings = new();

    public CleanerPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _settings = await AppSettings.LoadAsync(); await CheckInterruptedAsync(); };
    }

    private async Task CheckInterruptedAsync()
    {
        var state = await _service.LoadInterruptedStateAsync();
        if (state is not null)
            StatusText.Text = $"A previous cleanup ({state.Removed} files removed) was interrupted. " +
                              $"{state.RemainingFiles.Count} files may remain.";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Detecting browsers and measuring items…";
        BrowserPanel.Children.Clear();
        _itemBoxes.Clear();
        try
        {
            var scans = await _service.DetectAndScanAsync();
            foreach (var scan in scans.Where(s => s.Installed))
                BrowserPanel.Children.Add(BuildCard(scan));

            var installed = scans.Count(s => s.Installed);
            StatusText.Text = installed == 0
                ? "No supported browsers were detected on this PC."
                : $"{installed} browser(s) detected. Tick items to clean, then choose Clean selected.";
            CleanButton.IsEnabled = installed > 0;
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

    private Expander BuildCard(BrowserScan scan)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon
        {
            Glyph = "\uE774",
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
        header.Children.Add(new TextBlock
        {
            Text = scan.Installed ? "Installed" : "N/A",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(scan.Installed
                ? global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)
                : global::Windows.UI.Color.FromArgb(255, 0x9A, 0xA6, 0xA1))
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = scan.Installed,
            IsEnabled = scan.Installed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (!scan.Installed) return expander;

        var content = new StackPanel { Spacing = 2 };
        foreach (var item in scan.Items)
        {
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                Foreground = new SolidColorBrush(item.Destructive
                    ? global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D)
                    : global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            var detail = item.Bytes > 0
                ? $"{AppNotifications.FormatBytes(item.Bytes)} · {item.FileCount:N0} files"
                : "nothing to clean";
            text.Children.Add(new TextBlock
            {
                Text = item.Destructive ? $"{detail} · {item.Description}" : detail,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

            var box = new CheckBox
            {
                Content = text,
                // Destructive items start unticked so a single mis-click can never wipe data.
                IsChecked = !item.Destructive,
                MinHeight = 30
            };
            ToolTipService.SetToolTip(box, item.Description);
            _itemBoxes.Add((scan.Id, item.Id, item.Destructive, box));
            content.Children.Add(box);
        }

        expander.Content = content;
        return expander;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _itemBoxes
            .Where(x => x.Box.IsChecked == true)
            .Select(x => (x.BrowserId, x.ItemId))
            .ToArray();
        if (selected.Length == 0) { StatusText.Text = "Nothing is ticked. Tick at least one item to clean."; return; }

        var destructive = _itemBoxes.Count(x => x.Box.IsChecked == true && x.Destructive);
        if (destructive > 0)
        {
            var confirm = new ContentDialog
            {
                Title = "Delete user data?",
                Content = $"{destructive} destructive item(s) are ticked (cookies, history, saved passwords, or similar). " +
                          "That data is permanently deleted and cannot be undone. Continue?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        CleanButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Cleaning…";
        try
        {
            var secureDelete = SecureDeleteCheck.IsChecked == true
                ? new SecureDeleteOptions(_settings.SecureDeleteMethod, _settings.CustomWipePasses)
                : null;
            var report = await _service.CleanItemsAsync(selected, secureDelete);
            StatusText.Text = $"Complete: {report.Result.ItemsRemoved:N0} file(s) removed, " +
                              $"{AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered, " +
                              $"{report.Skipped.Count:N0} skipped.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
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
