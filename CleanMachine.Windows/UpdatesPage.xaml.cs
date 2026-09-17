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
        Progress.IsIndeterminate = true;
        StatusText.Text = "Checking for updates...";
        UpdateCheckResult result;
        try
        {
            result = await _service.CheckAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        finally
        {
            CheckButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }

        if (result.Error is not null) { StatusText.Text = result.Error; return; }
        if (!result.Available) { StatusText.Text = "You are running the latest version."; return; }

        // Skip our own confirmation when the user opted in; Windows still shows its
        // administrator-permission prompt at install time.
        var settings = await AppSettings.LoadAsync();
        if (!settings.SkipUpdateConfirmation)
        {
            var dialog = new ContentDialog
            {
                Title = $"Version {result.Manifest!.Version} available",
                Content = $"{result.Manifest.ReleaseNotes}\n\nCleanMachine will download, verify and install this update, then restart. Windows may ask for administrator permission.",
                PrimaryButtonText = "Update Now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                StatusText.Text = $"Version {result.Manifest!.Version} is available. Use Check for Updates when you're ready.";
                return;
            }
        }

        await RunUpdateAsync(result.Package!);
    }

    /// <summary>One-click flow: download (with a real progress bar) -> verify -> install.
    /// Verification is automatic; a declined elevation prompt keeps the package staged
    /// for a retry.</summary>
    private async Task RunUpdateAsync(UpdatePackage package)
    {
        CheckButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        DetailText.Visibility = Visibility.Collapsed;
        try
        {
            var download = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"Downloading update... {p:P0}";
            });
            StatusText.Text = "Downloading update...";
            _stagedPackagePath = await _service.DownloadAndVerifyAsync(package, download);

            Progress.IsIndeterminate = true;
            StatusText.Text = "Verified. Installing...";
            await InstallStagedAsync();
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = ex.Message;
            InstallButton.Visibility = Visibility.Visible; // allow a retry
        }
        catch (Exception ex) { StatusText.Text = $"Update failed: {ex.Message}"; }
        finally
        {
            CheckButton.IsEnabled = true;
            InstallButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_stagedPackagePath)) { StatusText.Text = "No staged package found."; return; }

        var settings = await AppSettings.LoadAsync();
        if (!settings.SkipUpdateConfirmation)
        {
            var confirm = new ContentDialog
            {
                Title = "Install update?",
                Content = "CleanMachine will install the staged update and restart. Windows may ask for administrator permission.",
                PrimaryButtonText = "Install Now",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.Text = "Installing...";
        try
        {
            await InstallStagedAsync();
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = ex.Message;
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

    /// <summary>Installs the staged package and reports the outcome. The .exe installer
    /// replaces the running app, so the app exits; MSIX installs in place. Throws
    /// OperationCanceledException if the elevation prompt is declined.</summary>
    private async Task InstallStagedAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path not found.");
        var isExe = _stagedPackagePath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        await _service.InstallVerifiedPackageAsync(_stagedPackagePath, executable);
        InstallButton.Visibility = Visibility.Collapsed;
        if (isExe)
        {
            StatusText.Text = "Installer launched. CleanMachine will now close to finish updating.";
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        else
        {
            StatusText.Text = "Update installed. Please restart CleanMachine.";
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
