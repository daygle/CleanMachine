using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanMachine.Windows;

public sealed partial class StartupPage : Page
{
    private readonly StartupAppsService _service = new();
    private IReadOnlyList<StartupApp> _apps = [];

    public StartupPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Scanning startup entries…";
        StartupList.Children.Clear();
        try
        {
            _apps = await _service.ScanAsync();

            // Group by location so the list reads like Task Manager's tabs.
            foreach (var group in _apps.GroupBy(a => a.LocationLabel).OrderBy(g => g.Key))
            {
                StartupList.Children.Add(new TextBlock
                {
                    Text = $"{group.Key.ToUpperInvariant()} ({group.Count()})",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x7F, 0x91, 0x89)),
                    Margin = new Thickness(0, 10, 0, 2)
                });

                foreach (var app in group)
                    StartupList.Children.Add(BuildRow(app));
            }

            StatusText.Text = _apps.Count == 0
                ? "No startup entries were found."
                : $"{_apps.Count} startup entr{(_apps.Count == 1 ? "y" : "ies")} found. Disabling keeps the program installed; removing deletes the auto-start entry only.";
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

    private StackPanel BuildRow(StartupApp app)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            MinHeight = 44,
            Opacity = app.Enabled ? 1.0 : 0.55,
            Padding = new Thickness(0, 4, 0, 4)
        };

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x27, 0x36, 0x30))
        });
        texts.Children.Add(new TextBlock
        {
            Text = app.Command,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 430,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0x89, 0x95, 0x8F))
        });

        var toggle = new ToggleSwitch
        {
            IsOn = app.Enabled,
            OnContent = "On",
            OffContent = "Off",
            Margin = new Thickness(0, -6, 0, -6)
        };
        // Events cannot be attached inside an object initializer, so wire Toggled here.
        toggle.Toggled += async (s, _) =>
        {
            toggle.IsEnabled = false;
            try
            {
                await _service.ToggleAsync(app);
                StatusText.Text = $"{app.Name} {(toggle.IsOn ? "enabled" : "disabled")}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                toggle.IsOn = !toggle.IsOn; // revert on failure
            }
            finally
            {
                toggle.IsEnabled = true;
            }
        };
        row.Children.Add(toggle);
        row.Children.Add(texts);

        var remove = new Button
        {
            Content = "Remove",
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        remove.Click += async (s, _) =>
        {
            var confirm = new ContentDialog
            {
                Title = $"Remove '{app.Name}' from startup?",
                Content = "The program stays installed; only its automatic launch at logon is removed. This cannot be undone from CleanMachine.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            remove.IsEnabled = false;
            try
            {
                await _service.DeleteAsync(app);
                StatusText.Text = $"{app.Name} was removed from startup.";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                remove.IsEnabled = true;
            }
        };
        row.Children.Add(remove);

        return row;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}
