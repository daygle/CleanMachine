using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class RegistryCarePage : Page
{
    private readonly RegistryCareService _service = new();
    private IReadOnlyList<RegistryBackup> _lastBackups = [];

    public RegistryCarePage()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        RestoreButton.Visibility = Visibility.Collapsed;
        try
        {
            var review = await _service.ScanAsync();
            if (review.Findings.Count == 0) { StatusText.Text = "No low-risk findings were found."; return; }

            var list = new StackPanel { Spacing = 8 };
            foreach (var item in review.Findings)
            {
                var cleanable = RegistryCareService.IsCleanable(item);
                list.Children.Add(new CheckBox
                {
                    IsChecked = cleanable,
                    IsEnabled = cleanable,
                    Content = cleanable
                        ? $"{item.Path} · {item.Confidence}% confidence · {item.Reason}"
                        : $"{item.Path} · {item.Confidence}% confidence · {item.Reason} (not eligible for cleaning)",
                    Tag = item
                });
            }

            var dialog = new ContentDialog
            {
                Title = "Select registry items to clean",
                Content = new ScrollViewer { MaxHeight = 360, Content = list },
                PrimaryButtonText = "Back Up and Clean Selected",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var selected = list.Children.OfType<CheckBox>()
                .Where(x => x.IsChecked == true)
                .Select(x => (RegistryFinding)x.Tag!)
                .ToArray();
            if (selected.Length == 0) { StatusText.Text = "Nothing was selected to clean."; return; }

            var result = await _service.PrepareReviewAsync(selected);
            _lastBackups = result.Backups;

            if (result.Backups.Count == 0)
            {
                StatusText.Text = "No backup could be created, so nothing was cleaned. The backup is required as a restore point.";
                return;
            }

            var clean = await _service.CleanAsync(result);
            var backupNote = $"{result.Backups.Count} backup file(s) saved under " +
                             $"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Backups")}";
            StatusText.Text = clean.Removed == 0 && clean.Skipped.Count == 0
                ? $"Nothing needed cleaning. {backupNote}."
                : $"Cleaned {clean.Removed} registry item(s); {clean.Skipped.Count} skipped. {backupNote}.";
            RestoreButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { ScanButton.IsEnabled = true; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBackups.Count == 0) { StatusText.Text = "No backup available to restore."; return; }

        var confirm = new ContentDialog
        {
            Title = "Restore registry backups?",
            Content = $"This will re-import {_lastBackups.Count} backup file(s) from {_lastBackups[0].CreatedAt:g}. Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var restored = 0;
            var failures = new List<string>();
            foreach (var backup in _lastBackups)
            {
                try { await RegistryCareService.RestoreBackupAsync(backup); restored++; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(backup.FilePath)}: {ex.Message}"); }
            }
            StatusText.Text = failures.Count == 0
                ? $"All {restored} registry backup(s) restored successfully."
                : $"Restored {restored} of {_lastBackups.Count}. Failures: {string.Join("; ", failures)}";
        }
        catch (Exception ex) { StatusText.Text = $"Restore failed: {ex.Message}"; }
    }
}
