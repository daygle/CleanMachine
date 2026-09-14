using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class SchedulesPage : Page
{
    /// <summary>Registry Care categories a schedule may clean (see RegistryFinding.Category).</summary>
    private static readonly string[] RegistryCategories =
        ["Installer/Uninstaller", "File Extensions", "MUI Cache", "Windows Startup", "Sound AppEvents"];

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
        BuildItemsPanel();
        RebuildList();
    }

    private void BuildItemsPanel()
    {
        ItemsPanel.Children.Clear();
        _itemBoxes.Clear();

        AddItemBox("browser", "Browser Caches", "Clears Chrome, Edge, and Firefox cache files (locked files are skipped)");

        foreach (var group in WindowsCleanupService.Catalog.GroupBy(c => c.Group))
        {
            ItemsPanel.Children.Add(SectionLabel(group.Key));
            foreach (var category in group)
                AddItemBox($"win:{category.Id}", category.Name, category.Description);
        }

        ItemsPanel.Children.Add(SectionLabel("Registry Care"));
        foreach (var category in RegistryCategories)
            AddItemBox($"reg:{category}", category, "Read-only scan; only low-risk items are cleaned, always after a backup");
    }

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
            var infoPanel = new StackPanel { Spacing = 2 };
            infoPanel.Children.Add(new TextBlock
            {
                Text = schedule.Enabled ? schedule.Name : $"{schedule.Name}  (paused)",
                FontSize = 13,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{schedule.TriggerSummary()} · {DescribeAction(schedule.AfterClean)}",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });

            var runButton = new Button
            {
                Content = "Run Now",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 28,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = schedule
            };
            var captured = schedule;
            runButton.Click += async (_, _) => await RunNowAsync(captured, runButton);

            var selectButton = new Button
            {
                Content = infoPanel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8)
            };
            selectButton.Click += (_, _) => Select(captured, isNew: false);

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            Grid.SetColumn(selectButton, 0);
            Grid.SetColumn(runButton, 1);
            row.Children.Add(selectButton);
            row.Children.Add(runButton);
            ScheduleList.Children.Add(row);
        }
    }

    private static string DescribeAction(ScheduleAction action) => action switch
    {
        ScheduleAction.Notify => "notify",
        ScheduleAction.Shutdown => "shut down",
        ScheduleAction.Restart => "restart",
        ScheduleAction.Sleep => "sleep",
        _ => "do nothing"
    };

    private void Add_Click(object sender, RoutedEventArgs e)
        => Select(new CleanupSchedule { Name = $"Cleanup {DateTime.Now:MMM d}" }, isNew: true);

    private void Select(CleanupSchedule schedule, bool isNew)
    {
        _current = schedule;
        _currentId = isNew ? null : schedule.Id;

        EditorHint.Visibility = Visibility.Collapsed;
        EditorFields.Visibility = Visibility.Visible;
        EditorHeadline.Text = isNew ? "New schedule" : "Edit schedule";
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

        NameBox.Text = schedule.Name;
        EnabledCheck.IsChecked = schedule.Enabled;
        TriggerCombo.SelectedIndex = (int)schedule.Trigger;
        HourBox.Value = schedule.Hour;
        MinuteBox.Value = schedule.Minute;
        DayOfWeekCombo.SelectedIndex = (int)schedule.DayOfWeek;
        DayOfMonthBox.Value = schedule.DayOfMonth;
        AfterCombo.SelectedIndex = (int)schedule.AfterClean;
        SecureDeleteCheck.IsChecked = schedule.SecureDelete;
        SecureDeleteHint.Visibility = schedule.SecureDelete ? Visibility.Visible : Visibility.Collapsed;
        WakeCheck.IsChecked = schedule.WakeToRun;

        _loadingItems = true;
        SelectAllItems.IsChecked = false;
        foreach (var (key, box) in _itemBoxes)
            box.IsChecked = key switch
            {
                "browser" => schedule.CleanBrowserCache,
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

    private async Task RunNowAsync(CleanupSchedule schedule, Button button)
    {
        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "Running…";
        try
        {
            var result = await ScheduleService.RunAsync(schedule, _settings);
            StatusText.Text = $"'{schedule.Name}' completed: {result.ItemsRemoved:N0} items, {AppNotifications.FormatBytes(result.BytesRecovered)} recovered"
                + (result.Issues.Count > 0 ? $" ({result.Issues.Count} skipped)" : ".");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Run failed: {ex.Message}";
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    private void SecureDelete_Changed(object sender, RoutedEventArgs e)
        => SecureDeleteHint.Visibility = SecureDeleteCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

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
            SecureDelete = SecureDeleteCheck.IsChecked == true,
            WakeToRun = WakeCheck.IsChecked == true && TriggerCombo.SelectedIndex != (int)ScheduleTrigger.AtLogon,
            CleanBrowserCache = _itemBoxes.Any(b => b.Key == "browser" && b.Box.IsChecked == true),
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
        EditorHeadline.Text = "Edit schedule";
        RebuildList();

        StatusText.Text = !schedule.Enabled
            ? "Saved. The schedule is paused."
            : registered
                ? "Saved. The task is registered with Windows Task Scheduler."
                : "Saved, but Windows could not register the task. Check Task Scheduler permissions.";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentId is null) return;

        _settings.Schedules.RemoveAll(s => s.Id == _currentId);
        await _settings.SaveAsync();
        await ScheduleService.UnregisterAsync(_currentId);

        _current = null;
        _currentId = null;
        EditorFields.Visibility = Visibility.Collapsed;
        EditorHint.Visibility = Visibility.Visible;
        EditorHeadline.Text = "No schedule selected";
        RebuildList();
        StatusText.Text = string.Empty;
    }
}
