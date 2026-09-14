using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class ActivityPage : Page
{
    private readonly ActivityStore _store = new();
    private IReadOnlyList<ActivityEntry> _allEntries = [];

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

    private static Border BuildActivityCard(ActivityEntry entry)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // info
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // time

        // Activity type icon
        var iconInfo = ActivityIcon(entry);
        var iconBorder = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(iconInfo.BackgroundColor),
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

        // Info stack: title + detail
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 500
        });
        Grid.SetColumn(info, 1);

        // Time badge
        var timePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        timePanel.Children.Add(new TextBlock
        {
            Text = entry.Time.ToString("h:mm tt"),
            FontSize = 12,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x4B, 0x77, 0x69)),
            HorizontalAlignment = HorizontalAlignment.Right
        });
        Grid.SetColumn(timePanel, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(timePanel);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1)
        };
    }

    private static (string Glyph, global::Windows.UI.Color BackgroundColor, global::Windows.UI.Color ForegroundColor) ActivityIcon(ActivityEntry entry)
    {
        var title = entry.Title.AsSpan();
        if (title.Contains("cache", StringComparison.OrdinalIgnoreCase) || title.Contains("clean", StringComparison.OrdinalIgnoreCase))
            return ("\uE74D", ColorFromHex("#E4F0F3"), ColorFromHex("#286E58")); // broom → green
        if (title.Contains("monitor", StringComparison.OrdinalIgnoreCase) || title.Contains("system", StringComparison.OrdinalIgnoreCase))
            return ("\uE730", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // heartbeat → teal (normal completed event)
        if (title.Contains("scan", StringComparison.OrdinalIgnoreCase) || title.Contains("registry", StringComparison.OrdinalIgnoreCase))
            return ("\uEA18", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // database → green
        if (title.Contains("update", StringComparison.OrdinalIgnoreCase))
            return ("\uE896", ColorFromHex("#E4F0F3"), ColorFromHex("#286E58")); // download → green
        if (title.Contains("startup", StringComparison.OrdinalIgnoreCase) || title.Contains("launch", StringComparison.OrdinalIgnoreCase))
            return ("\uE7E8", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // power → teal (normal completed event)
        return ("\uE81C", ColorFromHex("#E4F0F3"), ColorFromHex("#4B7769")); // clock → green (default)
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
