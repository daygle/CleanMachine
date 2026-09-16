using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class StartupAppsPage : Page
{
    private readonly StartupAppsService _service = new();
    private IReadOnlyList<StartupEntry> _entries = [];

    public StartupAppsPage()
    {
        InitializeComponent();
        // Scan automatically when the page is opened.
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        EmptyState.Visibility = Visibility.Collapsed;
        EntryPanel.Children.Clear();
        ListLabel.Text = "SCANNING…";

        try
        {
            _entries = await Task.Run(() => _service.Scan());
            RenderEntries();
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void RenderEntries()
    {
        EntryPanel.Children.Clear();

        var total = _entries.Count;
        var enabled = _entries.Count(e => e.Enabled);
        var orphans = _entries.Count(e => e.IsOrphan);

        TotalCount.Text = $"{total} {(total == 1 ? "entry" : "entries")}";
        EnabledCount.Text = $"{enabled} enabled";

        if (orphans > 0)
        {
            OrphanCount.Text = $"{orphans} orphaned";
            OrphanBadge.Visibility = Visibility.Visible;
        }
        else
        {
            OrphanBadge.Visibility = Visibility.Collapsed;
        }

        if (total == 0)
        {
            ListLabel.Text = "NO ENTRIES FOUND";
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        ListLabel.Text = "STARTUP ENTRIES";

        // Index 0 keeps the section-grouped view; the others render one flat, sorted
        // list. SortBox can be null very early in initialization.
        if ((SortBox?.SelectedIndex ?? 0) > 0)
        {
            RenderSortedFlat(SortBox!.SelectedIndex);
            return;
        }

        // Group by section for clean visual separation
        var sections = _entries
            .GroupBy(e => e.Section)
            .OrderBy(g => g.Key);

        foreach (var section in sections)
        {
            // Section header
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 6)
            };
            header.Children.Add(new TextBlock
            {
                Text = section.Key.ToUpperInvariant(),
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = $"({section.Count()})",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x9A, 0xA6, 0xA1)),
                VerticalAlignment = VerticalAlignment.Center
            });
            EntryPanel.Children.Add(header);

            foreach (var entry in section)
                EntryPanel.Children.Add(BuildEntryRow(entry));
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_entries.Count > 0)
            RenderEntries();
    }

    /// <summary>Renders a flat, ungrouped entry list. 1 = Name (A–Z),
    /// 2 = Status (enabled first, then name).</summary>
    private void RenderSortedFlat(int sortIndex)
    {
        var sorted = sortIndex == 2
            ? _entries.OrderByDescending(e => e.Enabled)
                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            : _entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sorted)
            EntryPanel.Children.Add(BuildEntryRow(entry));
    }

    private Border BuildEntryRow(StartupEntry entry)
    {
        // Main row grid: [toggle] [info] [action]
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // toggle
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // info
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // actions

        // Toggle switch
        var toggle = new ToggleSwitch
        {
            IsOn = entry.Enabled,
            MinWidth = 44,
            OnContent = "On",
            OffContent = "Off",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = entry
        };
        toggle.Toggled += OnToggle;
        Grid.SetColumn(toggle, 0);

        // Info stack: name + command path
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        info.Children.Add(BuildNameBlock(entry));
        info.Children.Add(BuildCommandBlock(entry));
        Grid.SetColumn(info, 1);

        // Remove button
        var removeBtn = new Button
        {
            Content = "Remove",
            Padding = new Thickness(14, 5, 14, 5),
            FontSize = 12,
            Tag = entry,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        removeBtn.Click += OnRemove;
        var btnContainer = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        btnContainer.Children.Add(removeBtn);
        Grid.SetColumn(btnContainer, 2);

        grid.Children.Add(toggle);
        grid.Children.Add(info);
        grid.Children.Add(btnContainer);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            Background = entry.IsOrphan
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFD, 0xF4, 0xF0)) // light peach for orphans
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Margin = new Thickness(0, 1, 0, 1)
        };
    }

    private static StackPanel BuildNameBlock(StartupEntry entry)
    {
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Source badge + orphan warning in a horizontal row
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        metaRow.Children.Add(new TextBlock
        {
            Text = SourceBadge(entry),
            FontSize = 10,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89))
        });

        if (entry.IsOrphan)
        {
            metaRow.Children.Add(new TextBlock
            {
                Text = "⚠ executable not found",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D))
            });
        }

        stack.Children.Add(metaRow);
        return stack;
    }

    private static TextBlock BuildCommandBlock(StartupEntry entry)
    {
        var tb = new TextBlock
        {
            Text = TruncateCommand(entry.Command),
            FontSize = 11,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(tb, entry.Command);
        return tb;
    }

    private static string SourceBadge(StartupEntry entry) => entry.Source switch
    {
        StartupSource.RegistryCurrentUser => "Current user registry",
        StartupSource.RegistryLocalMachine => "All users registry",
        StartupSource.StartupFolder => "Startup folder",
        _ => ""
    };

    /// <summary>Show the first 80 chars of a command, enough to identify the program
    /// without cluttering the row with a full registry path.</summary>
    private static string TruncateCommand(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Length > 80 ? trimmed[..77] + "…" : trimmed;
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not StartupEntry entry) return;
        var newEnabled = toggle.IsOn;

        var success = await Task.Run(() => _service.ToggleEnabled(entry, newEnabled));
        if (!success)
        {
            // Revert the toggle on failure
            toggle.Toggled -= OnToggle;
            toggle.IsOn = !newEnabled;
            toggle.Toggled += OnToggle;
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not StartupEntry entry) return;

        var confirm = new ContentDialog
        {
            Title = "Remove startup entry?",
            Content = $"This will permanently remove \"{entry.Name}\" from startup. The program itself will not be uninstalled.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var success = false;
        string? error = null;
        try { success = _service.Remove(entry, out error); }
        catch (Exception ex) { error = ex.Message; }
        if (!success)
        {
            var failure = new ContentDialog
            {
                Title = "Could not remove entry",
                Content = error ?? "The entry could not be removed.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await failure.ShowAsync();
            return;
        }

        // Re-scan to refresh the list
        ScanButton.IsEnabled = false;
        try
        {
            _entries = await Task.Run(() => _service.Scan());
            RenderEntries();
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }
}
