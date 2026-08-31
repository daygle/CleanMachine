using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class MainWindow : Window
{
    private readonly CleanupService _cleanup = new();
    private readonly WindowsCleanupService _windowsCleanup = new();
    private readonly UpdateService _updates = new();
    private readonly PrototypeState _state = new();
    private readonly ActivityStore _activityStore = new();
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        _ = LoadSettingsAsync();
    }

    private async void CleanNow_Click(object sender, RoutedEventArgs e)
    {
        CleanNowButton.IsEnabled = false;
        SystemStatusText.Text = "●  CLEANING IN PROGRESS";
        var result = await _cleanup.CleanSelectedBrowsersAsync();
        _state.LastCleanup = DateTimeOffset.Now;
        _state.Record("Browser cleanup completed", $"{result.ItemsRemoved:N0} items removed");
        await _activityStore.AddAsync(new ActivityEntry(DateTimeOffset.Now, "Browser cleanup completed", $"{result.ItemsRemoved:N0} items removed"));
        CleanNowButton.IsEnabled = true;
        SystemStatusText.Text = "●  ALL SYSTEMS CLEAR";
        var dialog = new ContentDialog
        {
            Title = "Cleanup complete",
            Content = $"{result.ItemsRemoved:N0} items removed and {result.BytesRecovered / 1024d / 1024d:N0} MB recovered. Saved passwords and bookmarks were not touched.",
            CloseButtonText = "Done",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await AppSettings.LoadAsync();
        _state.BackgroundAgentEnabled = _settings.BackgroundAgentEnabled;
        _state.CleanOnBrowserExit = _settings.CleanOnBrowserExit;
        _state.CheckForUpdatesAutomatically = _settings.CheckForUpdatesAutomatically;
        AgentStatusText.Text = _settings.BackgroundAgentEnabled ? "●  Background agent  ON" : "●  Background agent  OFF";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var result = await _updates.CheckAsync();
        var message = result.Error ?? (result.Available ? $"Version {result.Manifest!.Version} is available.\\n\\n{result.Manifest.ReleaseNotes}" : "You are running the latest version.");
        var dialog = new ContentDialog { Title = result.Available ? "Update available" : "CleanMachine updates" , Content = message, PrimaryButtonText = result.Available ? "Download and install" : "Done", CloseButtonText = result.Available ? "Later" : null, XamlRoot = Content.XamlRoot };
        if (result.Available && await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                SystemStatusText.Text = "●  DOWNLOADING UPDATE";
                var package = await _updates.DownloadAndVerifyAsync(result.Manifest!);
                await _updates.LaunchInstallerAsync(package);
                _state.Record("Update downloaded", $"Version {result.Manifest!.Version} is ready");
                SystemStatusText.Text = "●  UPDATE READY TO INSTALL";
            }
            catch (Exception ex)
            {
                await new ContentDialog { Title = "Update could not be installed", Content = ex.Message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
            }
        }
        else if (!result.Available) await dialog.ShowAsync();
    }

    private async void WindowsCleanup_Click(object sender, RoutedEventArgs e) => await ShowSectionInfoAsync("Windows Cleanup", "Scan safe temporary files, caches, diagnostics, and other review-first cleanup categories.");
    private async void Cleaner_Click(object sender, RoutedEventArgs e) => await ShowSectionInfoAsync("Browser Cleaner", "Chrome, Edge, and Firefox are protected by the background agent when configured.");
    private async void Activity_Click(object sender, RoutedEventArgs e) => await ShowSectionInfoAsync("Recent Activity", "Cleanup history will appear here as the native cleanup engine is connected.");
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSectionInfoAsync("Settings", "Choose protected browsers, cleanup categories, startup behavior, and update preferences.");

    private async Task ShowSectionInfoAsync(string title, string message)
    {
        await new ContentDialog { Title = title, Content = message, CloseButtonText = "Done", XamlRoot = Content.XamlRoot }.ShowAsync();
    }

    private async void WindowsCleanupScan_Click(object sender, RoutedEventArgs e)
    {
        var findings = await _windowsCleanup.ScanAsync();
        var content = new StackPanel { Spacing = 8 };
        foreach (var finding in findings)
        {
            var check = new CheckBox { IsChecked = finding.Selected, Content = $"{finding.Name} · {FormatBytes(finding.Bytes)} · {finding.Risk}", Tag = finding };
            content.Children.Add(check);
        }
        var dialog = new ContentDialog { Title = "Review Windows cleanup", Content = content, PrimaryButtonText = "Clean selected", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var selected = content.Children.OfType<CheckBox>().Select(c => (WindowsCleanupFinding)c.Tag!).Select(f => f with { Selected = content.Children.OfType<CheckBox>().First(c => ReferenceEquals(c.Tag, f)).IsChecked == true });
            var result = await _windowsCleanup.CleanSelectedAsync(selected);
            await new ContentDialog { Title = "Windows cleanup complete", Content = $"{result.ItemsRemoved:N0} files removed and {FormatBytes(result.BytesRecovered)} recovered. Locked and protected files were skipped.", CloseButtonText = "Done", XamlRoot = Content.XamlRoot }.ShowAsync();
        }
    }

    private async void SecureDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SsdWarningCheck.IsChecked != true)
        {
            await new ContentDialog { Title = "Confirmation required", Content = "Secure overwriting is not guaranteed on SSDs. Confirm that you understand this limitation before continuing.", CloseButtonText = "OK", XamlRoot = Content.XamlRoot }.ShowAsync();
            return;
        }
        var method = (WipeMethod)WipeMethodCombo.SelectedIndex;
        var customPasses = (int)CustomPassesBox.Value;
        var options = new SecureDeleteOptions(method, customPasses, true);
        var dialog = new ContentDialog { Title = "Secure Delete is ready", Content = $"The next explicitly selected file cleanup will use {options.Method} ({options.Passes} pass{(options.Passes == 1 ? "" : "es")}). No automatic cleanup will use this mode.", CloseButtonText = "Done", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private static string FormatBytes(long bytes) => bytes switch { < 1024 => $"{bytes} B", < 1024 * 1024 => $"{bytes / 1024d:N0} KB", _ => $"{bytes / 1024d / 1024d:N1} MB" };

    private async void RegistryScan_Click(object sender, RoutedEventArgs e)
    {
        var findings = await _cleanup.ScanRegistrySafelyAsync();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = findings.Count == 0 ? "No low-risk registry leftovers were found." : $"{findings.Count} findings are ready for review. No changes were made.", TextWrapping = TextWrapping.Wrap });
        foreach (var finding in findings.Take(12))
        {
            content.Children.Add(new CheckBox { IsChecked = true, Content = $"{finding.Hive}\\{finding.Path} — {finding.Reason}" });
        }
        var dialog = new ContentDialog
        {
            Title = "Review registry findings",
            Content = content,
            PrimaryButtonText = findings.Count == 0 ? "Done" : "Create backup & review",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
