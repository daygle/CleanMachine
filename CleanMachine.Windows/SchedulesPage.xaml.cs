using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class SchedulesPage : Page
{
    private readonly List<(string Key, CheckBox Box)> _itemBoxes = [];
    private AppSettings _settings = new();
    private CleanupSchedule? _current;
    private string? _currentId;
    // Guards the Select-all handler while a schedule's items are loaded into the boxes.
    private bool _loadingItems;

    public SchedulesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        // Only browsers and apps present on this PC are offered; the probes touch
        // the disk, so they run off the UI thread.
        var browsers = await Task.Run(() => BrowserCatalog.InstalledCacheBrowsers());
        var apps = await Task.Run(() => AppCleanupService.InstalledApps());
        BuildItemsPanel(browsers, apps);
        RebuildList();
    }

    /// <summary>Lists everything a schedule can clean, grouped under the page each
    /// item comes from, with that page's own sub-groups beneath.</summary>
    private void BuildItemsPanel(IReadOnlyList<BrowserDefinition> browsers, IReadOnlyList<AppDefinition> apps)
    {
        ItemsPanel.Children.Clear();
        _itemBoxes.Clear();

        ItemsPanel.Children.Add(ParentLabel("Browser Cleaner - Internet Cache",
            "Cache only - history, cookies and passwords are never touched. Files the browser is using are skipped."));
        foreach (var browser in browsers)
            AddItemBox($"browser:{browser.Id}", browser.Name, "Clears this browser's Internet Cache (every profile)");
        if (browsers.Count == 0)
            ItemsPanel.Children.Add(Hint("No supported browsers were found on this PC."));

        ItemsPanel.Children.Add(ParentLabel("Application Cleanup - Temp Files",
            "Temporary, cache and log files listed for each app on the Application Cleanup page."));
        foreach (var group in apps.GroupBy(a => a.Group))
        {
            ItemsPanel.Children.Add(SectionLabel(group.Key));
            foreach (var app in group)
                AddItemBox($"app:{app.Id}", app.Name, "Clears this app's temp and cache files");
        }
        if (apps.Count == 0)
            ItemsPanel.Children.Add(Hint("No supported applications were found on this PC."));

        ItemsPanel.Children.Add(ParentLabel("Windows Cleanup", null));
        foreach (var group in WindowsCleanupService.Catalog.GroupBy(c => c.Group))
        {
            ItemsPanel.Children.Add(SectionLabel(group.Key));
            foreach (var category in group)
                AddItemBox($"win:{category.Id}", category.Name, category.Description);
        }

        ItemsPanel.Children.Add(ParentLabel("Registry Care",
            "Only low-risk items are cleaned, always after a backup."));
        // Single source of truth (shared with Overview Quick Clean) so new registry
        // categories appear here automatically.
        foreach (var category in QuickCleanService.RegistryCategories)
            AddItemBox($"reg:{category}", category, "Read-only scan; only low-risk items are cleaned, always after a backup");
    }

    private static StackPanel ParentLabel(string text, string? explanation)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        if (!string.IsNullOrWhiteSpace(explanation))
            panel.Children.Add(Hint(explanation));
        return panel;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 8, 0, 0),
        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69))
    };

    private void AddItemBox(string key, string label, string description)
    {
        var box = new CheckBox { Content = label, Tag = key, MinHeight = 28 };
        ToolTipService.SetToolTip(box, description);
        _itemBoxes.Add((key, box));
        ItemsPanel.Children.Add(box);
    }

    private void RebuildList()
    {
        ScheduleList.Children.Clear();
        if (_settings.Schedules.Count == 0)
        {
            ScheduleList.Children.Add(new TextBlock
            {
                Text = "No schedules yet.",
                FontSize = 12,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            return;
        }

        foreach (var schedule in _settings.Schedules)
        {
            var captured = schedule;

            var infoPanel = new StackPanel { Spacing = 2 };
            infoPanel.Children.Add(new TextBlock
            {
                Text = schedule.Enabled ? schedule.Name : $"{schedule.Name}  (paused)",
                FontSize = 13,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{schedule.TriggerSummary()} - {DescribeAction(schedule.AfterClean)}",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

            var selectButton = new Button
            {
                Content = infoPanel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8)
            };
            selectButton.Click += (_, _) => Select(captured, isNew: false);

            var editButton = new Button
            {
                Content = "Edit",
                FontSize = 12,
                Padding = new Thickness(12, 0, 12, 0),
                // Stretch so it matches the select card's height and the actions read as
                // paired with the row instead of small buttons floating alongside it.
                VerticalAlignment = VerticalAlignment.Stretch
            };
            editButton.Click += (_, _) => Select(captured, isNew: false);

            var deleteButton = new Button
            {
                Content = "Delete",
                FontSize = 12,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            deleteButton.Click += async (_, _) => await DeleteScheduleAsync(captured.Id);

            var row = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            Grid.SetColumn(selectButton, 0);
            Grid.SetColumn(editButton, 1);
            Grid.SetColumn(deleteButton, 2);
            row.Children.Add(selectButton);
            row.Children.Add(editButton);
            row.Children.Add(deleteButton);
            ScheduleList.Children.Add(row);
        }
    }

    private static string DescribeAction(ScheduleAction action) => action switch
    {
        ScheduleAction.Notify => "Notify",
        ScheduleAction.Shutdown => "Shut Down",
        ScheduleAction.Restart => "Restart",
        ScheduleAction.Sleep => "Sleep",
        _ => "Do Nothing"
    };

    private void Add_Click(object sender, RoutedEventArgs e)
        // New schedules start with an empty Name field - no generated "Cleanup Sep 24"
        // prefill - so the user names it from scratch (Save still requires a name).
        => Select(new CleanupSchedule { Name = string.Empty }, isNew: true);

    private void Select(CleanupSchedule schedule, bool isNew)
    {
        _current = schedule;
        _currentId = isNew ? null : schedule.Id;

        EditorHint.Visibility = Visibility.Collapsed;
        EditorFields.Visibility = Visibility.Visible;
        EditorHeadline.Text = isNew ? "New Schedule" : "Edit Schedule";
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

        NameBox.Text = schedule.Name;
        EnabledCheck.IsChecked = schedule.Enabled;
        TriggerCombo.SelectedIndex = (int)schedule.Trigger;
        HourBox.Value = schedule.Hour;
        MinuteBox.Value = schedule.Minute;
        DayOfWeekCombo.SelectedIndex = (int)schedule.DayOfWeek;
        DayOfMonthBox.Value = schedule.DayOfMonth;
        AfterCombo.SelectedIndex = (int)schedule.AfterClean;
        WakeCheck.IsChecked = schedule.WakeToRun;

        _loadingItems = true;
        SelectAllItems.IsChecked = false;
        foreach (var (key, box) in _itemBoxes)
            box.IsChecked = key switch
            {
                // Schedules saved before browsers/apps could be chosen have no list:
                // they cleaned Chrome, Edge and Firefox, and every detected app.
                var k when k.StartsWith("browser:") => schedule.CleanBrowserCache
                    && (schedule.BrowserIds is { } chosen
                        ? chosen.Contains(k[8..], StringComparer.OrdinalIgnoreCase)
                        : _settings.ProtectedBrowsers.Contains(k[8..])),
                var k when k.StartsWith("app:") => schedule.CleanAppTempFiles
                    && (schedule.AppIds?.Contains(k[4..], StringComparer.OrdinalIgnoreCase) ?? true),
                var k when k.StartsWith("win:") => schedule.WindowsCategoryIds.Contains(k[4..], StringComparer.OrdinalIgnoreCase),
                var k when k.StartsWith("reg:") => schedule.RegistryCategories.Contains(k[4..], StringComparer.OrdinalIgnoreCase),
                _ => false
            };
        _loadingItems = false;

        UpdateTriggerVisibility();
        UpdateAfterWarning();
        StatusText.Text = string.Empty;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingItems) return;
        var value = SelectAllItems.IsChecked == true;
        foreach (var (_, box) in _itemBoxes)
            box.IsChecked = value;
    }

    private void Trigger_Changed(object sender, SelectionChangedEventArgs e) => UpdateTriggerVisibility();

    private void UpdateTriggerVisibility()
    {
        if (EditorFields.Visibility != Visibility.Visible) return;
        var trigger = (ScheduleTrigger)Math.Max(0, TriggerCombo.SelectedIndex);
        TimePanel.Visibility = trigger == ScheduleTrigger.AtLogon ? Visibility.Collapsed : Visibility.Visible;
        DayOfWeekCombo.Visibility = trigger == ScheduleTrigger.Weekly ? Visibility.Visible : Visibility.Collapsed;
        DayOfMonthBox.Visibility = trigger == ScheduleTrigger.Monthly ? Visibility.Visible : Visibility.Collapsed;
        // Waking from sleep only applies to a timed trigger; a logon task fires when
        // the user is already signed in.
        var showWake = trigger != ScheduleTrigger.AtLogon;
        WakeCheck.Visibility = showWake ? Visibility.Visible : Visibility.Collapsed;
        WakeHint.Visibility = showWake && WakeCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Wake_Changed(object sender, RoutedEventArgs e)
        => WakeHint.Visibility = WakeCheck.Visibility == Visibility.Visible && WakeCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void After_Changed(object sender, SelectionChangedEventArgs e) => UpdateAfterWarning();

    private void UpdateAfterWarning()
    {
        var action = (ScheduleAction)Math.Max(0, AfterCombo.SelectedIndex);
        AfterWarning.Visibility = action is ScheduleAction.Shutdown or ScheduleAction.Restart or ScheduleAction.Sleep
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "Give the schedule a name."; return; }

        // Ticks for browsers/apps not installed right now are kept, so reinstalling
        // one brings the choice back.
        var browserIds = TickedIds("browser:");
        if (_current.CleanBrowserCache && _current.BrowserIds is { } previousBrowsers)
            browserIds.AddRange(previousBrowsers.Where(id => !IsShown("browser:", id) && !browserIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        var appIds = TickedIds("app:");
        if (_current.CleanAppTempFiles && _current.AppIds is { } previousApps)
            appIds.AddRange(previousApps.Where(id => !IsShown("app:", id) && !appIds.Contains(id, StringComparer.OrdinalIgnoreCase)));

        var schedule = new CleanupSchedule
        {
            Id = _currentId ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Enabled = EnabledCheck.IsChecked == true,
            Trigger = (ScheduleTrigger)Math.Max(0, TriggerCombo.SelectedIndex),
            Hour = double.IsNaN(HourBox.Value) ? 0 : (int)Math.Clamp(HourBox.Value, 0, 23),
            Minute = double.IsNaN(MinuteBox.Value) ? 0 : (int)Math.Clamp(MinuteBox.Value, 0, 59),
            DayOfWeek = (DayOfWeek)Math.Max(0, DayOfWeekCombo.SelectedIndex),
            DayOfMonth = double.IsNaN(DayOfMonthBox.Value) ? 1 : (int)Math.Clamp(DayOfMonthBox.Value, 1, 31),
            AfterClean = (ScheduleAction)Math.Max(0, AfterCombo.SelectedIndex),
            WakeToRun = WakeCheck.IsChecked == true && TriggerCombo.SelectedIndex != (int)ScheduleTrigger.AtLogon,
            CleanBrowserCache = browserIds.Count > 0,
            BrowserIds = browserIds,
            CleanAppTempFiles = appIds.Count > 0,
            AppIds = appIds,
            WindowsCategoryIds = _itemBoxes.Where(b => b.Key.StartsWith("win:") && b.Box.IsChecked == true).Select(b => b.Key[4..]).ToList(),
            RegistryCategories = _itemBoxes.Where(b => b.Key.StartsWith("reg:") && b.Box.IsChecked == true).Select(b => b.Key[4..]).ToList()
        };

        if (!ScheduledTask.HasWork(schedule))
        {
            StatusText.Text = "Tick at least one item for the schedule to clean.";
            return;
        }

        var index = _settings.Schedules.FindIndex(s => s.Id == schedule.Id);
        if (index >= 0) _settings.Schedules[index] = schedule;
        else _settings.Schedules.Add(schedule);
        await _settings.SaveAsync();

        var registered = true;
        if (schedule.Enabled) registered = await ScheduleService.RegisterAsync(schedule);
        else await ScheduleService.UnregisterAsync(schedule.Id);

        _currentId = schedule.Id;
        _current = schedule;
        DeleteButton.Visibility = Visibility.Visible;
        EditorHeadline.Text = "Edit Schedule";
        RebuildList();

        StatusText.Text = !schedule.Enabled
            ? "Saved. The schedule is paused."
            : registered
                ? "Saved. The task is registered with Windows Task Scheduler."
                : "Saved, but Windows could not register the task. Check Task Scheduler permissions.";
    }

    private List<string> TickedIds(string prefix)
        => _itemBoxes
            .Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal) && b.Box.IsChecked == true)
            .Select(b => b.Key[prefix.Length..])
            .ToList();

    private bool IsShown(string prefix, string id)
        => _itemBoxes.Any(b => b.Key.Equals(prefix + id, StringComparison.OrdinalIgnoreCase));

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId is null) return;
        await DeleteScheduleAsync(_currentId);
    }

    private async Task DeleteScheduleAsync(string id)
    {
        var schedule = _settings.Schedules.FirstOrDefault(s => s.Id == id);
        if (schedule is null) return;

        _settings.Schedules.RemoveAll(s => s.Id == id);
        await _settings.SaveAsync();
        await ScheduleService.UnregisterAsync(id);

        if (_currentId == id)
        {
            _current = null;
            _currentId = null;
            EditorFields.Visibility = Visibility.Collapsed;
            EditorHint.Visibility = Visibility.Visible;
            EditorHeadline.Text = "No Schedule Selected";
            StatusText.Text = string.Empty;
        }
        RebuildList();
    }
}
