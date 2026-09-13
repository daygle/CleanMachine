using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class InstalledAppsPage : Page
{
    private readonly InstalledAppsService _service = new();
    private IReadOnlyList<InstalledApp> _apps = [];

    public InstalledAppsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Enumerating installed applications…";
        AppsPanel.Children.Clear();
        try
        {
            _apps = await _service.ScanAsync();
            Render(_apps);
            var shown = _apps.Count(a => !a.SystemComponent);
            StatusText.Text = $"{shown} application{(shown == 1 ? "" : "s")} found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(IReadOnlyList<InstalledApp> apps)
    {
        AppsPanel.Children.Clear();
        foreach (var app in apps.Where(a => !a.SystemComponent))
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                MinHeight = 52,
                Padding = new Thickness(0, 4, 0, 4)
            };

            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = app.DisplayName,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
            });
            var meta = string.Join(" · ", new[]
            {
                app.DisplayVersion,
                app.Publisher,
                app.EstimatedSizeBytes > 0 ? WindowsCleanupPage.FormatBytes(app.EstimatedSizeBytes) : null,
                app.InstallDate is null ? null : $"installed {app.InstallDate}"
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            texts.Children.Add(new TextBlock
            {
                Text = meta.Length == 0 ? app.Scope : $"{meta} · {app.Scope}",
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 470,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
            });
            row.Children.Add(texts);

            if (app.ModifyPath is not null)
            {
                var modify = new Button
                {
                    Content = "Modify",
                    Padding = new Thickness(12, 6, 12, 6),
                    VerticalAlignment = VerticalAlignment.Center
                };
                modify.Click += async (s, _) => await LaunchAsync(app, modify, uninstall: false);
                row.Children.Add(modify);
            }

            if (app.UninstallString is not null || app.Kind == AppEntryKind.Store)
            {
                var uninstall = new Button
                {
                    Content = "Uninstall",
                    Padding = new Thickness(12, 6, 12, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC7, 0x77, 0x5D)),
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF))
                };
                uninstall.Click += async (s, _) => await LaunchAsync(app, uninstall, uninstall: true);
                row.Children.Add(uninstall);
            }

            AppsPanel.Children.Add(row);
        }
    }

    private async Task LaunchAsync(InstalledApp app, Button button, bool uninstall)
    {
        var verb = uninstall ? "Uninstall" : "Modify";
        var confirm = new ContentDialog
        {
            Title = $"{verb} '{app.DisplayName}'?",
            Content = "The vendor's own installer will open and take over from here. CleanMachine does not remove the program's files itself.",
            PrimaryButtonText = verb,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        button.IsEnabled = false;
        StatusText.Text = $"Launching {verb.ToLowerInvariant()} for {app.DisplayName}…";
        try
        {
            if (uninstall) await _service.LaunchUninstallAsync(app);
            else await _service.LaunchModifyAsync(app);
            StatusText.Text = $"{app.DisplayName}: the vendor's {verb.ToLowerInvariant()} program was launched.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Filter from the cached full list; no re-enumeration.
        var term = SearchBox.Text.Trim();
        if (term.Length == 0) { Render(_apps); return; }
        Render(_apps.Where(a =>
            a.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (a.Publisher?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || (a.DisplayVersion?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList());
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}
