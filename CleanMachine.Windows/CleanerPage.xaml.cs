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
    // Detected browsers and the one whose detail is currently shown on the right.
    private IReadOnlyList<BrowserScan> _scans = [];
    private BrowserScan? _detailScan;

    public CleanerPage()
    {
        InitializeComponent();
        // Load monitoring settings, then scan browsers automatically when opened.
        Loaded += async (_, _) => { _settings = await AppSettings.LoadAsync(); LoadMonitoring(); await CheckInterruptedAsync(); await ScanAsync(); };
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
        var hasDestructive = BrowserCatalog.ItemsFor(browserDefinition.Family).Any(i => i.Destructive);
        if (hasDestructive)
            panel.Children.Add(new TextBlock
            {
                Text = "Ticking a destructive item (cookies, history, passwords) lets the automatic close-clean remove it without asking. These stay off by default.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E)),
                Margin = new Thickness(0, 0, 0, 4)
            });
        foreach (var item in BrowserCatalog.ItemsFor(browserDefinition.Family))
        {
            // Destructive items can now be opted into for the automatic close-clean.
            // They remain unticked unless the user explicitly enables them, and are
            // flagged so the risk is clear.
            var box = new CheckBox
            {
                Content = item.Destructive ? $"{item.Name}  (destructive)" : item.Name,
                IsChecked = effective.Contains(item.Id),
                MinHeight = 26
            };
            if (item.Destructive)
            {
                box.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xB0, 0x3A, 0x2E));
                ToolTipService.SetToolTip(box, "Destructive: when ticked, this is removed automatically every time the browser closes.");
            }
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

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    /// <summary>Detects browsers, builds the left list, and shows the first browser's
    /// detail. <paramref name="statusOverride"/> preserves a caller's message (e.g. a
    /// post-clean summary) instead of the detection summary.</summary>
    private async Task ScanAsync(string? statusOverride = null)
    {
        ScanButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Detecting browsers and measuring items…";
        BrowserPanel.Children.Clear();
        _itemBoxes.Clear();
        _detailScan = null;
        DetailBackButton.Visibility = Visibility.Collapsed;
        try
        {
            _scans = await _service.DetectAndScanAsync();
            var installed = _scans.Where(s => s.Installed).ToList();
            foreach (var scan in installed)
                BrowserPanel.Children.Add(BuildCard(scan));

            StatusText.Text = statusOverride ?? (installed.Count == 0
                ? "No supported browsers were detected on this PC."
                : $"{installed.Count} browser(s) detected. Tick items to clean, then choose Clean Selected.");
            CleanButton.IsEnabled = installed.Count > 0;

            var first = installed.FirstOrDefault();
            if (first is not null) ShowBrowserDetail(first);
            else ShowDetailPlaceholder();
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
            Glyph = "",
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
            Text = scan.Items.Count > 0 ? AppNotifications.FormatBytes(totalBytes) : "Clean",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
        });

        var expander = new Expander
        {
            Header = header,
            IsExpanded = scan.Items.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var content = new StackPanel { Spacing = 2 };
        foreach (var item in scan.Items)
        {
            var current = item;
            // Remember the user's tick choice across navigation/restarts; fall back
            // to the safe default (destructive items start unticked). Set IsChecked
            // before wiring the handlers so restoring state does not itself save.
            var key = $"{scan.Id}:{item.Id}";
            var box = new CheckBox
            {
                IsChecked = _settings.BrowserCleanupSelection.TryGetValue(key, out var saved)
                    ? saved
                    : !item.Destructive,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            box.Checked += (_, _) => RememberSelection(key, true);
            box.Unchecked += (_, _) => RememberSelection(key, false);
            _itemBoxes.Add((scan.Id, item.Id, item.Destructive, box));

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
                Text = detail,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

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
            detailsButton.Click += (_, _) => ShowItemDetail(scan, current);
            ToolTipService.SetToolTip(detailsButton, item.Description);

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
        expander.Expanding += (_, _) => ShowBrowserDetail(scan);
        return expander;
    }

    /// <summary>Browser-level detail: summary chips plus one read-only framed row per
    /// item (size and file count), each drilling into that item's files on click.
    /// Selection lives only in the left list, so the right card never duplicates the
    /// checkboxes.</summary>
    private void ShowBrowserDetail(BrowserScan scan)
    {
        _detailScan = scan;
        DetailBackButton.Visibility = Visibility.Collapsed;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = "BROWSER";
        DetailHeadline.Text = scan.Name;
        DetailSubHeadline.Text = "Click an item to see the files it will clean. Tick items in the list on the left.";
        DetailPanel.Children.Clear();

        if (scan.Items.Count == 0)
        {
            SetChips(null, null, null);
            DetailPanel.Children.Add(BuildDetailPlaceholder("Nothing to clean for this browser."));
            return;
        }

        SetChips(AppNotifications.FormatBytes(scan.Items.Sum(i => i.Bytes)),
            scan.Items.Sum(i => i.FileCount).ToString("N0"),
            scan.Items.Count.ToString());

        foreach (var item in scan.Items)
        {
            var current = item;

            // The right card is a read-only breakdown: it shows each item and drills
            // into its files. Selection lives only in the left list, so there is no
            // duplicate checkbox here.
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                Text = item.Name,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(item.Destructive
                    ? global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D)
                    : global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            label.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            body.Content = label;
            body.Click += (_, _) => ShowItemDetail(scan, current);
            ToolTipService.SetToolTip(body, item.Description);
            Grid.SetColumn(body, 0);
            row.Children.Add(body);

            var side = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            side.Children.Add(new TextBlock
            {
                Text = item.Bytes > 0 ? AppNotifications.FormatBytes(item.Bytes) : "-",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58))
            });
            side.Children.Add(new TextBlock
            {
                Text = $"{item.FileCount:N0} file(s)",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            Grid.SetColumn(side, 1);
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

    /// <summary>Item-level detail: the actual files the item would clean (with sizes),
    /// plus a caution for destructive items. The Back button returns to the browser's
    /// item list.</summary>
    private void ShowItemDetail(BrowserScan scan, BrowserItemInfo item)
    {
        _detailScan = scan;
        DetailBackButton.Visibility = Visibility.Visible;
        DetailGroupBadge.Visibility = Visibility.Visible;
        DetailGroupBadgeText.Text = "BROWSER";
        DetailHeadline.Text = $"{scan.Name} - {item.Name}";
        DetailSubHeadline.Text = item.Description;
        SetChips(item.Bytes > 0 ? AppNotifications.FormatBytes(item.Bytes) : "-", item.FileCount.ToString("N0"), "1");
        DetailPanel.Children.Clear();

        if (item.Destructive)
            DetailPanel.Children.Add(DetailRow("Caution",
                "Deletes personal data such as cookies, history or saved passwords. Cleaned automatically only if you opt this item in on the close-monitor; ticking it here removes it permanently."));

        const int shown = 200;
        var files = _service.ListItemFiles(scan.Id, item.Id, shown + 1)
            .OrderByDescending(f => f.Bytes)
            .ToList();
        if (files.Count == 0)
        {
            DetailPanel.Children.Add(BuildDetailPlaceholder("No files in this location right now."));
            return;
        }

        foreach (var file in files.Take(shown))
            DetailPanel.Children.Add(FileRow(file.Path, file.Bytes));

        if (item.FileCount > shown)
            DetailPanel.Children.Add(new TextBlock
            {
                Text = $"+ {item.FileCount - shown:N0} more file(s)",
                FontSize = 10,
                Margin = new Thickness(4, 4, 0, 0),
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
    }

    private static Border FileRow(string path, long bytes)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = path,
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
            Text = AppNotifications.FormatBytes(bytes),
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

    private static Border DetailRow(string label, string value) => new()
    {
        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFB, 0xFD, 0xFC)),
        BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 10, 6),
        Child = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = label.ToUpperInvariant(), FontSize = 9, CharacterSpacing = 40,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)) },
                new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)) }
            }
        }
    };

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

    private void ShowDetailPlaceholder()
    {
        DetailHeadline.Text = "Browser details";
        DetailSubHeadline.Text = "No supported browsers were detected.";
        DetailGroupBadge.Visibility = Visibility.Collapsed;
        DetailBackButton.Visibility = Visibility.Collapsed;
        SetChips(null, null, null);
        DetailPanel.Children.Clear();
    }

    private void SetChips(string? size, string? files, string? items)
    {
        ChipSizeValue.Text = size ?? "—";
        ChipFilesValue.Text = files ?? "—";
        ChipItemsValue.Text = items ?? "—";
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e)
    {
        if (_detailScan is { } scan) ShowBrowserDetail(scan);
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
            var completion = $"Complete: {report.Result.ItemsRemoved:N0} file(s) removed, " +
                             $"{AppNotifications.FormatBytes(report.Result.BytesRecovered)} recovered, " +
                             $"{report.Skipped.Count:N0} skipped.";
            // Re-scan so the list and sizes reflect what was just cleaned, keeping the
            // completion message as the status.
            await ScanAsync(completion);
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
