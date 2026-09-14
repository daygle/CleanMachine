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
        UpdateExitItemsButton(ChromeItemsButton, "chrome");
        UpdateExitItemsButton(EdgeItemsButton, "edge");
        UpdateExitItemsButton(FirefoxItemsButton, "firefox");
        CloseBrowsersCheck.IsChecked = _settings.CloseOpenBrowsersAutomatically;
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

    /// <summary>Shows which items a browser's exit-clean covers: the count, or the
    /// picker's implicit default before the user has configured anything.</summary>
    private void UpdateExitItemsButton(Button button, string browser)
    {
        var monitor = _settings.FindBrowserMonitor(browser);
        button.Content = monitor?.Items is { } chosen
            ? $"{chosen.Count} item(s)…"
            : "Safe items (default)…";
    }

    /// <summary>Lets the user pick exactly which items a browser's after-exit clean
    /// covers. Safe (non-destructive) catalog items are offered and saved
    /// immediately on confirm; destructive items are listed greyed out so it is
    /// visible they can never be auto-cleaned. Saving an explicit set (even an
    /// empty one) marks the browser as configured; Cancel changes nothing.</summary>
    private async void ChooseExitItems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string browser }) return;
        var monitor = _settings.FindBrowserMonitor(browser);
        var browserDefinition = BrowserCatalog.Find(browser);
        if (monitor is null || browserDefinition is null) return;

        var effective = _settings.EffectiveExitItems(browser);
        var panel = new StackPanel { Spacing = 6 };
        var boxes = new List<(string Id, CheckBox Box)>();
        foreach (var item in BrowserCatalog.ItemsFor(browserDefinition.Family))
        {
            var box = new CheckBox
            {
                Content = item.Name,
                IsChecked = effective.Contains(item.Id),
                IsEnabled = !item.Destructive,
                MinHeight = 26
            };
            if (item.Destructive)
                ToolTipService.SetToolTip(box, "Destructive - cleaned only manually on this page, never automatically.");
            boxes.Add((item.Id, box));
            panel.Children.Add(box);
        }

        var displayName = browserDefinition.Name;
        var dialog = new ContentDialog
        {
            Title = $"{displayName} - items cleaned when it closes",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        monitor.Items = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();
        UpdateExitItemsButton((Button)sender, browser);
        UpdateMonitorHint();
    }

    private async void CloseBrowsers_Changed(object sender, RoutedEventArgs e)
    {
        if (!_monitorReady) return;
        _settings.CloseOpenBrowsersAutomatically = CloseBrowsersCheck.IsChecked == true;
        await _settings.SaveAsync();
    }

    private void UpdateMonitorHint()
    {
        MonitoringHint.Text = _settings.CleanOnBrowserExit
            ? "Cleans the items chosen per browser (safe items by default). The browser that just closed is cleaned even if other browsers are still open; passwords, cookies, history, and other destructive items are never cleaned automatically. CleanMachine runs in the background and starts with Windows so exits are detected."
            : "Monitoring is off - browsers are only cleaned when you run it manually here.";
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

        // Close-assist: when enabled, offer to close running browsers instead of
        // refusing. Two-tier: graceful close first (like clicking the window's X),
        // force-kill only for whatever is still alive after the 5s wait. Declining
        // here falls back to the default behavior (the clean below refuses).
        if (_settings.CloseOpenBrowsersAutomatically)
        {
            var running = BrowserCleanupService.GetRunningBrowsers();
            if (running.Count > 0)
            {
                var names = string.Join(", ", running.Select(BrowserCleanupService.DisplayNameForProcess).Distinct());
                var confirm = new ContentDialog
                {
                    Title = "Close running browsers?",
                    Content = $"Still running: {names}. They will be closed gracefully (like clicking the window's X), " +
                              "then anything still alive after 5 seconds is force-closed. Unsaved work may be lost. Continue?",
                    PrimaryButtonText = "Close and clean",
                    CloseButtonText = "Cancel",
                    XamlRoot = XamlRoot
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    StatusText.Text = $"Close these browsers before cleaning: {names}.";
                    return;
                }

                Progress.Visibility = Visibility.Visible;
                StatusText.Text = $"Closing {names}…";
                var stillRunning = await BrowserCleanupService.CloseRunningBrowsersAsync(running);
                if (stillRunning.Count > 0)
                {
                    Progress.Visibility = Visibility.Collapsed;
                    StatusText.Text = $"Could not close: {string.Join(", ", stillRunning.Select(BrowserCleanupService.DisplayNameForProcess))}. Close them manually and try again.";
                    return;
                }
            }
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
