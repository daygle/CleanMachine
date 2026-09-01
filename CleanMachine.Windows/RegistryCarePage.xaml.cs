using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class RegistryCarePage : Page
{
    private readonly RegistryCareService _service = new();
    private RegistryBackup? _lastBackup;

    public RegistryCarePage()
    {
        InitializeComponent();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try
        {
            var review = await _service.ScanAsync();
            if (review.Findings.Count == 0) { StatusText.Text = "No low-risk findings were found."; return; }

            var list = new StackPanel { Spacing = 8 };
            foreach (var item in review.Findings)
                list.Children.Add(new CheckBox
                {
                    IsChecked = true,
                    Content = $"{item.Path} · {item.Confidence}% confidence · {item.Reason}",
                    Tag = item
                });

            var dialog = new ContentDialog
            {
                Title = "Review registry findings",
                Content = list,
                PrimaryButtonText = "Create backup",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var selected = list.Children.OfType<CheckBox>()
                .Where(x => x.IsChecked == true)
                .Select(x => (RegistryFinding)x.Tag!);

            var result = await _service.PrepareReviewAsync(selected);
            _lastBackup = result.Backup;

            if (result.Backup is not null)
            {
                var valid = await RegistryCareService.ValidateBackupAsync(result.Backup);
                StatusText.Text = valid
                    ? $"Backup created and validated: {result.Backup.FilePath}. No registry values were changed."
                    : $"Backup created but validation failed: {result.Backup.FilePath}.";
                RestoreButton.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                StatusText.Text = "No backup was created (no eligible findings).";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { ScanButton.IsEnabled = true; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBackup is null) { StatusText.Text = "No backup available to restore."; return; }

        var confirm = new ContentDialog
        {
            Title = "Restore registry backup?",
            Content = $"This will import the backup from {_lastBackup.CreatedAt:g}. Continue?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await RegistryCareService.RestoreBackupAsync(_lastBackup);
            StatusText.Text = "Registry backup restored successfully.";
        }
        catch (Exception ex) { StatusText.Text = $"Restore failed: {ex.Message}"; }
    }
}
