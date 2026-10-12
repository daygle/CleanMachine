using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

/// <summary>One tickable row in an <see cref="ItemPicker"/>.</summary>
internal sealed record PickerItem(string Key, string Label, bool IsChecked, string? Tooltip = null);

/// <summary>Rows under an optional sub-group label (e.g. "Windows Explorer").</summary>
internal sealed record PickerGroup(string? Label, IReadOnlyList<PickerItem> Items);

/// <summary>One card in an <see cref="ItemPicker"/>, usually named after the page its
/// items come from. <paramref name="InSelectAll"/> false keeps a section out of the
/// dialog's Select All, for items that delete user data and must only ever be ticked
/// one by one (browser passwords, the Recycle Bin). <paramref name="IsWarning"/>
/// draws the card's heading, explanation and items in the warning colour.</summary>
internal sealed record PickerSection(
    string Id,
    string Heading,
    string? Explanation,
    IReadOnlyList<PickerGroup> Groups,
    string? EmptyNote = null,
    bool InSelectAll = true,
    bool IsWarning = false);

/// <summary>The item picker shared by every "choose what to clean" popup (Automatic
/// Cleanup triggers, browser close-cleaning, Overview Quick Clean), so they all look
/// and behave the same: an intro line, a Select All checkbox, one card per section
/// with a tri-state heading that selects or clears that card, optional sub-group
/// labels, and an optional footer note.</summary>
internal static class ItemPicker
{
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(global::Windows.UI.Color.FromArgb(255, r, g, b));

    /// <summary>Shows the picker and returns the ticked keys per section id, or null
    /// when the dialog was cancelled.</summary>
    public static async Task<IReadOnlyDictionary<string, HashSet<string>>?> ShowAsync(
        XamlRoot xamlRoot,
        string title,
        string? intro,
        IReadOnlyList<PickerSection> sections,
        string? footer = null,
        string selectAllLabel = "Select All")
    {
        var panel = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(intro)) panel.Children.Add(Note(intro));

        var selectAll = new CheckBox
        {
            Content = new TextBlock
            {
                Text = selectAllLabel,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(0x28, 0x6E, 0x58)
            }
        };
        panel.Children.Add(selectAll);

        var built = new List<(PickerSection Section, CheckBox Header, List<(string Key, CheckBox Box)> Items)>();
        foreach (var section in sections)
        {
            var (header, children) = AddCard(panel, section);
            var items = new List<(string Key, CheckBox Box)>();
            foreach (var group in section.Groups)
            {
                if (group.Items.Count == 0) continue;
                if (!string.IsNullOrWhiteSpace(group.Label)) children.Children.Add(GroupLabel(group.Label));
                foreach (var item in group.Items)
                {
                    var box = new CheckBox { Content = item.Label, IsChecked = item.IsChecked, MinHeight = 28 };
                    if (section.IsWarning) box.Foreground = Brush(0xB0, 0x3A, 0x2E);
                    if (!string.IsNullOrWhiteSpace(item.Tooltip)) ToolTipService.SetToolTip(box, item.Tooltip);
                    items.Add((item.Key, box));
                    children.Children.Add(box);
                }
            }
            if (items.Count == 0)
                children.Children.Add(Note(section.EmptyNote ?? "Nothing to choose from here - nothing was detected on this PC."));
            header.IsEnabled = items.Count > 0;
            built.Add((section, header, items));
        }

        if (!string.IsNullOrWhiteSpace(footer)) panel.Children.Add(Note(footer));

        // Keep every heading and Select All in step with the rows: checked when all
        // are, cleared when none are, indeterminate in between.
        var syncing = false;
        var selectAllItems = built.Where(b => b.Section.InSelectAll).SelectMany(b => b.Items).Select(i => i.Box).ToList();
        selectAll.IsEnabled = selectAllItems.Count > 0;
        void Refresh()
        {
            syncing = true;
            foreach (var (_, header, items) in built) header.IsChecked = State(items.Select(i => i.Box));
            selectAll.IsChecked = State(selectAllItems);
            syncing = false;
        }
        void Set(IEnumerable<CheckBox> boxes, bool value)
        {
            syncing = true;
            foreach (var box in boxes) box.IsChecked = value;
            syncing = false;
            Refresh();
        }

        selectAll.Click += (_, _) => Set(selectAllItems, selectAll.IsChecked == true);
        foreach (var (_, header, items) in built)
        {
            var boxes = items.Select(i => i.Box).ToList();
            header.Click += (_, _) => Set(boxes, header.IsChecked == true);
            foreach (var box in boxes)
            {
                box.Checked += (_, _) => { if (!syncing) Refresh(); };
                box.Unchecked += (_, _) => { if (!syncing) Refresh(); };
            }
        }
        Refresh();

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 460,
                Padding = new Thickness(0, 0, 12, 0)
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        return built.ToDictionary(
            b => b.Section.Id,
            b => b.Items.Where(i => i.Box.IsChecked == true).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Tri-state for a set of rows: all ticked, none ticked, or some.</summary>
    internal static bool? State(IEnumerable<CheckBox> boxes)
    {
        var list = boxes.ToList();
        var ticked = list.Count(b => b.IsChecked == true);
        return ticked == 0 ? false : ticked == list.Count ? true : null;
    }

    private static (CheckBox Header, StackPanel Children) AddCard(StackPanel parent, PickerSection section)
    {
        var headingBrush = section.IsWarning ? Brush(0xB0, 0x3A, 0x2E) : Brush(0x27, 0x36, 0x30);
        var header = new CheckBox
        {
            Content = new TextBlock
            {
                Text = section.Heading,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = headingBrush
            }
        };
        var children = new StackPanel { Spacing = 2, Margin = new Thickness(28, 4, 0, 0) };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(section.Explanation))
            body.Children.Add(new TextBlock
            {
                Text = section.Explanation,
                FontSize = 11,
                Foreground = section.IsWarning ? Brush(0xB0, 0x3A, 0x2E) : Brush(0x89, 0x95, 0x8F),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 0, 0)
            });
        body.Children.Add(children);
        parent.Children.Add(new Border
        {
            BorderBrush = section.IsWarning ? Brush(0xE8, 0xC4, 0xBE) : Brush(0xE5, 0xEB, 0xE7),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = body
        });
        return (header, children);
    }

    private static TextBlock GroupLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10,
        Foreground = Brush(0x7F, 0x91, 0x89),
        Margin = new Thickness(0, 8, 0, 2)
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush(0x89, 0x95, 0x8F),
        TextWrapping = TextWrapping.Wrap
    };
}
