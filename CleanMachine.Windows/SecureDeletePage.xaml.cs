using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
namespace CleanMachine.Windows;
public sealed partial class SecureDeletePage : Page
{
    private readonly SecureDeleteService _service = new(); private CancellationTokenSource? _cancel; private IReadOnlyList<SecureDeleteSelection> _selection = [];
    public SecureDeletePage() => InitializeComponent();
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (SsdAcknowledged.IsChecked != true) { StatusText.Text = "Acknowledge the SSD limitation first."; return; }
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow)); var files = await picker.PickMultipleFilesAsync(); if (files is null || files.Count == 0) return;
        _selection = await _service.PrepareSelectionAsync(files.Select(x => x.Path)); FileList.ItemsSource = _selection.Select(x => $"{(x.Selected ? "Eligible" : "Protected/read-only")} · {x.Path} · {x.Bytes:N0} bytes"); ChooseButton.Content = "Secure delete selected"; ChooseButton.Click -= Choose_Click; ChooseButton.Click += Delete_Click; StatusText.Text = $"Review {_selection.Count} selected files, then confirm deletion.";
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return; var confirm = new ContentDialog { Title = "Permanently delete selected files?", Content = "This cannot be undone. Protected and ineligible files will be skipped.", PrimaryButtonText = "Delete permanently", CloseButtonText = "Cancel", XamlRoot = XamlRoot }; if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        _cancel = new CancellationTokenSource(); ChooseButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.Visibility = Visibility.Visible; try { var progress = new Progress<CleanupProgress>(p => { Progress.Value = p.Total == 0 ? 0 : (double)p.Completed / p.Total; StatusText.Text = $"{p.Phase}: {p.Completed}/{p.Total}"; }); var result = await _service.DeleteAsync(_selection, new SecureDeleteOptions((WipeMethod)Method.SelectedIndex, 1, true), progress, _cancel.Token); StatusText.Text = $"Complete: {result.FilesProcessed} deleted, {result.FilesSkipped} skipped."; } catch (OperationCanceledException) { StatusText.Text = "Secure Delete cancelled."; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { ChooseButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancel.Dispose(); _cancel = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
}
