using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class DriveWiperPage : Page
{
    private readonly DriveWiperService _service = new();
    private IReadOnlyList<DriveWipeTarget> _drives = [];
    private CancellationTokenSource? _cancel;

    public DriveWiperPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { await LoadDrivesAsync(); await CheckInterruptedWipeAsync(); };
    }

    private Task LoadDrivesAsync()
    {
        _drives = DriveWiperService.ListDrives();
        DriveCombo.ItemsSource = _drives.Select(d => d.DisplayName).ToList();
        // Default to the Windows drive when present.
        var systemIndex = -1;
        for (var i = 0; i < _drives.Count; i++)
            if (_drives[i].IsSystemDrive) { systemIndex = i; break; }
        DriveCombo.SelectedIndex = systemIndex >= 0 ? systemIndex : _drives.Count > 0 ? 0 : -1;
        return Task.CompletedTask;
    }

    private async Task CheckInterruptedWipeAsync()
    {
        // A previous run that was killed mid-wipe can leave the temp file behind.
        await Task.Run(() => DriveWiperService.CleanupAbandonedWiperFiles());
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedIndex < 0 || DriveCombo.SelectedIndex >= _drives.Count) return;
        var target = _drives[DriveCombo.SelectedIndex];
        var passes = PassesCombo.SelectedIndex switch { 1 => 3, 2 => 7, _ => 1 };

        var confirm = new ContentDialog
        {
            Title = $"Wipe free space on {target.DisplayName}?",
            Content = $"{passes} pass(es) will overwrite {WindowsCleanupPage.FormatBytes(target.FreeBytes)} of unused space. " +
                      "This can take a long time on large or slow drives and cannot be cancelled without losing progress. Continue?",
            PrimaryButtonText = "Wipe",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        WipeButton.IsEnabled = false;
        DriveCombo.IsEnabled = false;
        PassesCombo.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        _cancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CleanupProgress>(p =>
            {
                Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total;
                StatusText.Text = $"{p.Phase}: {p.Completed:N0}/{p.Total:N0} MB written";
            });
            var result = await _service.WipeFreeSpaceAsync(target, passes, progress, _cancel.Token);
            StatusText.Text = $"Done: {result.Passes} pass(es), {WindowsCleanupPage.FormatBytes(result.BytesOverwritten)} overwritten in {result.Duration:hh\\:mm\\:ss}.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Wipe cancelled. Space already overwritten stays overwritten; run the wipe again to finish the job.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            WipeButton.IsEnabled = true;
            DriveCombo.IsEnabled = true;
            PassesCombo.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Progress.Visibility = Visibility.Collapsed;
            _cancel?.Dispose();
            _cancel = null;
            await LoadDrivesAsync(); // free-space figures changed
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
