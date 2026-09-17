using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class InstalledAppsPage : Page
{
    private readonly InstalledAppsService _service = new();
    private IReadOnlyList<InstalledApp> _allApps = [];
    private string _searchText = "";

    public InstalledAppsPage()
    {
        InitializeComponent();
        // Scan automatically when the page is opened.
        Loaded += (_, _) => Scan_Click(this, new RoutedEventArgs());
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        EmptyState.Visibility = Visibility.Collapsed;
        AppListPanel.Children.Clear();
        ListLabel.Text = "SCANNING...";

        try
        {
            _allApps = await Task.Run(() => _service.Scan());
            _searchText = SearchBox.Text ?? "";
            RenderList();
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchText = sender.Text ?? "";
        if (_allApps.Count > 0)
            RenderList();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_allApps.Count > 0)
            RenderList();
    }

    private void RenderList()
    {
        AppListPanel.Children.Clear();

        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _allApps
            : _allApps.Where(a => MatchesFilter(a, _searchText)).ToList();

        EntryCount.Text = $"{filtered.Count} of {_allApps.Count} apps";

        if (filtered.Count == 0)
        {
            ListLabel.Text = "NO MATCHING APPS";
            EmptyState.Visibility = Visibility.Visible;
            FooterText.Text = "";
            return;
        }

        ListLabel.Text = "APPLICATIONS";
        EmptyState.Visibility = Visibility.Collapsed;

        // Index 0 keeps the publisher-grouped view; the others render one flat,
        // sorted list. SortBox can be null very early in initialization.
        if ((SortBox?.SelectedIndex ?? 0) <= 0)
            RenderGroupedByPublisher(filtered);
        else
            RenderSortedFlat(filtered, SortBox!.SelectedIndex);

        FooterText.Text = $"{filtered.Count} application{(filtered.Count == 1 ? "" : "s")} found. " +
                          "Uninstall and Modify launch the vendor's own installer.";
    }

    private void RenderGroupedByPublisher(IReadOnlyList<InstalledApp> apps)
    {
        // Group by publisher for visual structure
        var groups = apps
            .GroupBy(a => string.IsNullOrWhiteSpace(a.Publisher) ? "Other" : a.Publisher)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            // Publisher section header
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 14, 0, 4)
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
            AppListPanel.Children.Add(header);

            foreach (var app in group)
                AppListPanel.Children.Add(BuildAppRow(app));
        }
    }

    private void RenderSortedFlat(IReadOnlyList<InstalledApp> apps, int sortIndex)
    {
        // 1 = Name (A-Z), 2 = Size (largest first), 3 = Recently installed.
        var sorted = sortIndex switch
        {
            2 => apps.OrderByDescending(a => a.EstimatedSize ?? -1)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            // InstallDate is an ISO "yyyy-MM-dd" string (or empty), so a descending
            // string sort orders newest first and pushes undated entries to the end.
            3 => apps.OrderByDescending(a => a.InstallDate ?? "", StringComparer.Ordinal)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            _ => apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
        };

        foreach (var app in sorted)
            AppListPanel.Children.Add(BuildAppRow(app));
    }

    private Border BuildAppRow(InstalledApp app)
    {
        // Main row: [icon-area] [info] [actions]
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // icon placeholder
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // info
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // actions

        // App icon placeholder (first letter in a colored circle)
        var iconBg = app.IsSystemComponent
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE8, 0xEB, 0xE9))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xE4, 0xF0, 0xF3));
        var iconFg = app.IsSystemComponent
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x28, 0x6E, 0x58));

        var iconBorder = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = iconBg,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = iconFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(iconBorder, 0);

        // Info stack: name + metadata line
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };

        // App name
        var nameBlock = new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        info.Children.Add(nameBlock);

        // Metadata line: version - size - install date - arch
        var meta = BuildMetadataLine(app);
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);

        // Actions: Modify (if has command) + Uninstall
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (!string.IsNullOrWhiteSpace(app.ModifyCommand) && !app.IsSystemComponent && app.Kind == AppEntryKind.Win32)
        {
            var modifyBtn = new Button
            {
                Content = "Modify",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12,
                Tag = app
            };
            modifyBtn.Click += OnModify;
            actions.Children.Add(modifyBtn);
        }
        if (!string.IsNullOrWhiteSpace(app.UninstallCommand) && !app.IsSystemComponent && app.Kind == AppEntryKind.Win32)
        {
            var uninstallBtn = new Button
            {
                Content = "Uninstall",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12,
                Tag = app
            };
            uninstallBtn.Click += OnUninstall;
            actions.Children.Add(uninstallBtn);
        }
        if (app.Kind == AppEntryKind.Store && !app.IsSystemComponent)
        {
            var storeRemoveBtn = new Button
            {
                Content = "Uninstall",
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 12,
                Tag = app
            };
            storeRemoveBtn.Click += OnUninstall;
            actions.Children.Add(storeRemoveBtn);
        }
        Grid.SetColumn(actions, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        var opacity = app.IsSystemComponent ? 0.6 : 1.0;
        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            Opacity = opacity,
            Margin = new Thickness(0, 1, 0, 1)
        };
        ToolTipService.SetToolTip(border, app.RegistryKey);
        return border;
    }

    private static StackPanel BuildMetadataLine(InstalledApp app)
    {
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        void AddPart(string text, string colorHex = "#89958F")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var parts = meta.Children;
            if (parts.Count > 0)
            {
                meta.Children.Add(new TextBlock
                {
                    Text = "-",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xD0, 0xDB, 0xD5)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            meta.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = ParseColor(colorHex),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200
            });
        }

        if (!string.IsNullOrWhiteSpace(app.Version))
            AddPart(app.Version);
        if (app.Kind == AppEntryKind.Store)
            AddPart("Store", "#4B7769");
        if (!string.IsNullOrWhiteSpace(app.Publisher)) AddPart(app.Publisher, "#53635B");
        if (app.EstimatedSize is > 0)
            AddPart(AppNotifications.FormatBytes(app.EstimatedSize.Value));
        if (!string.IsNullOrWhiteSpace(app.InstallDate))
            AddPart($"installed {app.InstallDate}");
        if (!string.IsNullOrWhiteSpace(app.Architecture))
            AddPart(app.Architecture);

        return meta;
    }

    private static bool MatchesFilter(InstalledApp app, string filter)
    {
        return app.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || app.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || app.Version.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnModify(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not InstalledApp app) return;
        _ = await Task.Run(() => _service.LaunchModify(app));
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not InstalledApp app) return;

        var confirm = new ContentDialog
        {
            Title = $"Uninstall {app.Name}?",
            Content = app.Kind == AppEntryKind.Store
                ? "This Microsoft Store package will be removed for the current user."
                : "This will launch the vendor's own uninstaller. CleanMachine does not perform the removal directly.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _ = await Task.Run(() => _service.LaunchUninstall(app));
    }

    private static SolidColorBrush ParseColor(string hex)
    {
        if (hex.StartsWith('#') && hex.Length == 7)
        {
            var r = Convert.ToByte(hex.Substring(1, 2), 16);
            var g = Convert.ToByte(hex.Substring(3, 2), 16);
            var b = Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
        }
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F));
    }
}
