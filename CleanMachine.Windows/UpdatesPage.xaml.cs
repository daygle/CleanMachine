using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanMachine.Windows;

public sealed partial class UpdatesPage : Page
{
    private readonly UpdateService _service = new();
    private readonly UpdateStateStore _stateStore = new();
    private string? _stagedPackagePath;

    public UpdatesPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CheckPendingUpdateAsync();
    }

    private async Task CheckPendingUpdateAsync()
    {
        var state = await _stateStore.LoadAsync();
        if (state is { Status: "staged" or "installing" } && !string.IsNullOrEmpty(state.PackagePath))
        {
            _stagedPackagePath = state.PackagePath;
            PendingText.Text = $"A pending update ({state.Status}) was found from a previous session.";
            PendingText.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Visible;
            StatusText.Text = "A verified package is staged and ready to install.";
            DetailText.Text = $"Package: {state.PackagePath}";
            DetailText.Visibility = Visibility.Visible;
        }

        if (await _stateStore.HasPendingUpdateAsync() == false
            && UpdateService.FindRollbackCopy() is not null)
        {
            RollbackButton.Visibility = Visibility.Visible;
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        try
        {
            var result = await _service.CheckAsync();
            if (result.Error is not null) { StatusText.Text = result.Error; return; }
            if (!result.Available) { StatusText.Text = "You are running the latest version."; return; }

            var dialog = new ContentDialog
            {
                Title = $"Version {result.Manifest!.Version} available",
                Content = result.Manifest.ReleaseNotes,
                PrimaryButtonText = "Download and verify",
                CloseButtonText = "Later",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            Progress.IsIndeterminate = true;
            StatusText.Text = "Downloading and verifying package…";
            var path = await _service.DownloadAndVerifyAsync(result.Package!);
            _stagedPackagePath = path;

            StatusText.Text = $"Verified package staged. Ready to install.";
            DetailText.Text = $"Package: {path}";
            DetailText.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally
        {
            CheckButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_stagedPackagePath)) { StatusText.Text = "No staged package found."; return; }

        var confirm = new ContentDialog
        {
            Title = "Install update?",
            Content = "The application will restart after installation. A rollback copy will be saved.",
            PrimaryButtonText = "Install now",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
            await _service.InstallVerifiedPackageAsync(_stagedPackagePath, executable);
            StatusText.Text = "Installation complete. Please restart the application.";
            InstallButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Installation failed: {ex.Message}. Rollback may be required.";
            RollbackButton.Visibility = Visibility.Visible;
        }
        finally
        {
            InstallButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        RollbackButton.IsEnabled = false;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
            var success = await _service.RollbackAsync(executable);
            StatusText.Text = success
                ? "Rollback complete. The previous version has been restored."
                : "No rollback copy was found or the current executable is missing.";
            if (success) RollbackButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { StatusText.Text = $"Rollback failed: {ex.Message}"; }
        finally { RollbackButton.IsEnabled = true; }
    }
}
