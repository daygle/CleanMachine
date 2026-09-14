using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class CleanerPage : Page
{
    private readonly BrowserCleanupService _service = new();
    private readonly List<(string BrowserId, string ItemId, bool Destructive, CheckBox Box)> _itemBoxes = [];
    private AppSettings _settings = new();
    // Guards the change handlers while the page loads settings into the controls.
    private bool _monitorReady;

    public CleanerPage()
    {
        InitializeComponent();
        // Load monitoring settings, then scan browsers automatically when opened.
        Loaded += async (_, _) => { _settings = await AppSettings.LoadAsync(); LoadMonitoring(); await CheckInterruptedAsync(); Scan_Click(this, new RoutedEventArgs()); };
    }

    /// <summary>Browser monitoring (clean cache on browser close) and the background
    /// agent it runs under live here on the Browser Cleaner page, next to the
    /// cleaning flow they control. Changes apply immediately - there is no save
    /// button on this page.</summary>
    private void LoadMonitoring()
    {
        _monitorReady = false;

        CleanToggle.IsChecked = _settings.CleanOnBrowserExit;
        var chrome = _settings.FindBrowserMonitor("chrome");
        var edge = _settings.FindBrowserMonitor("edge");
        var firefox = _settings.FindBrowserMonitor("firefox");
        ChromeEnabled.IsChecked = chrome?.Enabled ?? true;
        ChromeAction.SelectedIndex = ToComboIndex(chrome?.AfterExit ?? ExitAction.CleanAndNotify);
        EdgeEnabled.IsChecked = edge?.Enabled ?? true;
        EdgeAction.SelectedIndex = ToComboIndex(edge?.AfterExit ?? ExitAction.CleanAndNotify);
        FirefoxEnabled.IsChecked = firefox?.Enabled ?? true;
        FirefoxAction.SelectedIndex = ToComboIndex(firefox?.AfterExit ?? ExitAction.CleanAndNotify);
        UpdateMonitorHint();

        _monitorReady = true;
    }

    private async void CleanToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_monitorReady) return;
        _settings.CleanOnBrowserExit = CleanToggle.IsChecked == true;
        await _settings.SaveAsync();
        // Turning this on/off is what makes the agent (and Windows startup) needed,
        // so bring them in step immediately.
        (App.Current as App)?.ApplyBackgroundServices(_settings);
        UpdateMonitorHint();
    }

    private async void BrowserMonitor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_monitorReady) return;
        ApplyMonitor("chrome", ChromeEnabled, ChromeAction);
        ApplyMonitor("edge", EdgeEnabled, EdgeAction);
        ApplyMonitor("firefox", FirefoxEnabled, FirefoxAction);
        await _settings.SaveAsync();
        UpdateMonitorHint();
    }

    private void ApplyMonitor(string id, CheckBox enabled, ComboBox action)
    {
        var monitor = _settings.FindBrowserMonitor(id);
        if (monitor is null)
        {
            monitor = new BrowserMonitorSetting { Browser = id };
            _settings.BrowserMonitors.Add(monitor);
        }
        monitor.Enabled = enabled.IsChecked == true;
        monitor.AfterExit = FromComboIndex(action.SelectedIndex);
    }

    private static int ToComboIndex(ExitAction action) => action switch
    {
        ExitAction.DoNothing => 0,
        ExitAction.CleanSilently => 1,
        _ => 2
    };

    private static ExitAction FromComboIndex(int index) => index switch
    {
        0 => ExitAction.DoNothing,
        1 => ExitAction.CleanSilently,
        _ => ExitAction.CleanAndNotify
    };

    private void UpdateMonitorHint()
    {
        MonitoringHint.Text = _settings.CleanOnBrowserExit
            ? "Caches only. Open browsers are skipped; passwords, bookmarks, cookies, and history are never touched. CleanMachine runs in the background and starts with Windows so exits are detected."
            : "Monitoring is off - browser caches are only cleaned when you run it manually here.";
    }

    /// <summary>Persists one item's tick state so the Browser Cleaner page restores
    /// the user's selection next time. Best-effort - a failed save just means the
    /// default is used next time.</summary>
    private async void RememberSelection(string key, bool value)
    {
        try
        {
            _settings.BrowserCleanupSelection[key] = value;
            await _settings.SaveAsync();
        }
        catch { /* remembering the selection is best-effort */ }
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

            // Remember the user's tick choice across navigation/restarts; fall back
            // to the safe default (destructive items start unticked so a single
            // mis-click can never wipe data). Set IsChecked before wiring the
            // handlers so restoring the saved state does not itself trigger a save.
            var key = $"{scan.Id}:{item.Id}";
            var box = new CheckBox
            {
                Content = text,
                IsChecked = _settings.BrowserCleanupSelection.TryGetValue(key, out var saved)
                    ? saved
                    : !item.Destructive,
                MinHeight = 30
            };
            box.Checked += (_, _) => RememberSelection(key, true);
            box.Unchecked += (_, _) => RememberSelection(key, false);
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
            _ = new CleanupStatsStore().RecordAsync(report.Result.ItemsRemoved, report.Result.BytesRecovered);
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
