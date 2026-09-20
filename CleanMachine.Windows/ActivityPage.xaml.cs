using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class ActivityPage : Page
{
    private readonly ActivityStore _store = new();
    private IReadOnlyList<ActivityEntry> _allEntries = [];

    // Shared by every drill-down chevron: the hover handlers run on each pointer move,
    // so they must not allocate a new brush every time.
    private static readonly SolidColorBrush ChevronIdleBrush = new(ColorFromHex("#9AA6A1"));
    private static readonly SolidColorBrush ChevronActiveBrush = new(ColorFromHex("#4B7769"));

    public ActivityPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        ClearButton.IsEnabled = false;
        _allEntries = await _store.LoadAsync();
        RenderEntries();
        ClearButton.IsEnabled = true;
    }

    private void RenderEntries()
    {
        ActivityPanel.Children.Clear();

        var total = _allEntries.Count;
        var today = _allEntries.Count(e => e.Time.Date == DateTimeOffset.Now.Date);
        var yesterday = _allEntries.Count(e => e.Time.Date == DateTimeOffset.Now.Date.AddDays(-1));

        TotalCount.Text = $"{total} {(total == 1 ? "event" : "events")}";
        TodayCount.Text = $"{today} today";

        if (total == 0)
        {
            ListLabel.Text = "NO ACTIVITY";
            EmptyState.Visibility = Visibility.Visible;
            FooterText.Text = "";
            return;
        }

        ListLabel.Text = "RECENT ACTIVITY";
        EmptyState.Visibility = Visibility.Collapsed;

        // Group by relative date
        var groups = _allEntries
            .GroupBy(e => RelativeDate(e.Time))
            .ToList();

        foreach (var group in groups)
        {
            // Date section header
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 14, 0, 6)
            };
            header.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = $"({group.Count()})",
                FontSize = 10,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x9A, 0xA6, 0xA1)),
                VerticalAlignment = VerticalAlignment.Center
            });
            ActivityPanel.Children.Add(header);

            foreach (var entry in group)
                ActivityPanel.Children.Add(BuildActivityCard(entry));
        }

        FooterText.Text = $"{total} activity {(total == 1 ? "entry" : "entries")} recorded. Older entries are automatically pruned.";
    }

    /// <summary>Builds one activity row.
    /// Every row - with or without a drill-down - is laid out by the same four-column
    /// grid (icon | title+detail | time | chevron), and rows are never put inside an
    /// <c>Expander</c>. That matters for alignment: an Expander header reserves a column
    /// for its toggle button, so expandable rows used to push their time ~60 px left of
    /// plain rows; the reserved chevron column keeps every time on the same right edge.</summary>
    private static Border BuildActivityCard(ActivityEntry entry)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Pixel) }); // icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // title + detail
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // time
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Pixel) }); // drill-down chevron

        // Activity type icon
        var iconInfo = ActivityIcon(entry);
        var iconBorder = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(iconInfo.BackgroundColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = iconInfo.Glyph,
                FontSize = 14,
                Foreground = new SolidColorBrush(iconInfo.ForegroundColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(iconBorder, 0);

        // Info stack: title + detail. The detail must NOT set MaxWidth: a Stretch-aligned
        // element that is clamped by MaxWidth is centred in its column, which is what used
        // to push the detail line away from the title by an amount that varied per row.
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(new TextBlock
        {
            Text = entry.Detail,
            FontSize = 11,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(info, 1);

        // Date + time, right-aligned inside an Auto column so every row shares one right edge.
        var time = new TextBlock
        {
            Text = entry.Time.ToString("MMM d, h:mm tt"),
            FontSize = 12,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 2);

        var chevron = new FontIcon
        {
            Glyph = "\uE70D",
            FontSize = 12,
            Foreground = ChevronIdleBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 3);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(time);
        grid.Children.Add(chevron);

        var card = new Border
        {
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE5, 0xEB, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1)
        };

        if (entry.Details is not { Count: > 0 } details)
        {
            // No drill-down: the row is inert, so reserve the chevron column (empty) and
            // keep the plain grid as the card's only child.
            chevron.Visibility = Visibility.Collapsed;
            grid.Margin = new Thickness(14, 6, 10, 6);
            card.Child = grid;
            return card;
        }

        var content = new StackPanel { Spacing = 3, Margin = new Thickness(58, 0, 38, 8), Visibility = Visibility.Collapsed };
        foreach (var line in details)
            content.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x53, 0x63, 0x5B))
            });

        // The whole row toggles the drill-down (as the old Expander did), but the chevron
        // is drawn by our grid rather than by the control, so it cannot shift the columns.
        // Padding here matches the plain row's grid margin so both land on the same grid.
        var header = new Button
        {
            Content = grid,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 6, 10, 6),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        void ApplyToggleState(bool expanded)
        {
            content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                header, $"{(expanded ? "Hide" : "Show")} details for {entry.Title}");
        }
        ApplyToggleState(false);
        header.PointerEntered += (_, _) => chevron.Foreground = ChevronActiveBrush;
        header.PointerExited += (_, _) => chevron.Foreground = ChevronIdleBrush;
        header.Click += (_, _) => ApplyToggleState(content.Visibility != Visibility.Visible);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(content);
        card.Child = stack;
        return card;
    }

    private static (string Glyph, global::Windows.UI.Color BackgroundColor, global::Windows.UI.Color ForegroundColor) ActivityIcon(ActivityEntry entry)
    {
        var title = entry.Title.AsSpan();
        if (title.Contains("cache", StringComparison.OrdinalIgnoreCase) || title.Contains("clean", StringComparison.OrdinalIgnoreCase))
            return ("\uE74D", ColorFromHex("#E4F0F3"), ColorFromHex("#286E58")); // broom -> green
        if (title.Contains("monitor", StringComparison.OrdinalIgnoreCase) || title.Contains("system", StringComparison.OrdinalIgnoreCase))
            return ("\uE730", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // heartbeat -> teal (normal completed event)
        if (title.Contains("scan", StringComparison.OrdinalIgnoreCase) || title.Contains("registry", StringComparison.OrdinalIgnoreCase))
            return ("\uEA18", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // database -> green
        if (title.Contains("update", StringComparison.OrdinalIgnoreCase))
            return ("\uE896", ColorFromHex("#E4F0F3"), ColorFromHex("#286E58")); // download -> green
        if (title.Contains("startup", StringComparison.OrdinalIgnoreCase) || title.Contains("launch", StringComparison.OrdinalIgnoreCase))
            return ("\uE7E8", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // power -> teal (normal completed event)
        return ("\uE81C", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // clock -> green (default)
    }

    private static string RelativeDate(DateTimeOffset time)
    {
        var now = DateTimeOffset.Now;
        var diff = now.Date - time.Date;
        if (diff.Days == 0) return "Today";
        if (diff.Days == 1) return "Yesterday";
        if (diff.Days < 7) return $"Earlier this week";
        if (diff.Days < 30) return "Earlier this month";
        return "Older";
    }

    private static global::Windows.UI.Color ColorFromHex(string hex)
    {
        var r = Convert.ToByte(hex.Substring(1, 2), 16);
        var g = Convert.ToByte(hex.Substring(3, 2), 16);
        var b = Convert.ToByte(hex.Substring(5, 2), 16);
        return global::Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "Clear activity history?",
            Content = "This will remove all recorded activity. This cannot be undone.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await _store.ClearAsync();
        await LoadAsync();
    }
}
