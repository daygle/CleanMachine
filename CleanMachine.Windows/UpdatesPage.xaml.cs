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
        // Reconcile leftovers from a previous session. This used to be done inline
        // here, which meant the app carried stale "pending update" state everywhere
        // else; it now also runs at startup (see App.OnLaunched).
        var result = await UpdateStateStore.ReconcileAsync();

        // The detached MSIX helper records why an install failed; surface it before
        // anything else so a failed update is explained instead of looking like
        // nothing happened (the old version simply came back).
        var installError = UpdateStateStore.TakeInstallError();
        if (installError is not null)
        {
            StatusText.Text = "The last update did not install.";
            // The detached helper's raw PowerShell error is opaque. When Smart App
            // Control is enforcing it is almost always the cause - it blocks this
            // app's self-signed MSIX outright, which is what it did on the machine
            // that reported it - so lead with the reason and what to do about it
            // rather than making the user decode an Add-AppxPackage stack.
            DetailText.Text = UpdateService.IsSmartAppControlEnforcing
                ? UpdateService.SmartAppControlGuidance
                : installError;
            DetailText.Visibility = Visibility.Visible;
        }

        if (result.Pending is { } pending)
        {
            _stagedPackagePath = pending.PackagePath;
            var targetLabel = pending.TargetVersion is not null ? $" for version {pending.TargetVersion}" : "";
            PendingText.Text = $"A pending update ({pending.Status}){targetLabel} was found from a previous session.";
            PendingText.Visibility = Visibility.Visible;
            DismissButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Visible;
            if (installError is null)
            {
                StatusText.Text = "A verified package is staged and ready to install.";
                DetailText.Text = $"Package: {pending.PackagePath}";
                DetailText.Visibility = Visibility.Visible;
            }
        }

        if (result.Pending is null && UpdateService.FindRollbackCopy() is not null)
        {
            RollbackButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Clears the leftover staged package (state + downloaded file) and
    /// hides the pending-update UI, retiring an update the user no longer wants.</summary>
    private async Task DismissPendingUpdateAsync(string? packagePath = null)
    {
        await _stateStore.DismissAsync(packagePath ?? _stagedPackagePath);
        _stagedPackagePath = null;
        PendingText.Text = "";
        PendingText.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        DismissButton.Visibility = Visibility.Collapsed;
        DetailText.Text = "";
        DetailText.Visibility = Visibility.Collapsed;
    }

    private async void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        await DismissPendingUpdateAsync();
        StatusText.Text = "Pending update removed. Check for Updates to pick up anything new.";
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
        if (!result.Available)
        {
            StatusText.Text = "You are running the latest version.";
            await DismissPendingUpdateAsync(); // clear any obsolete pending update
            return;
        }

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

        await RunUpdateAsync(result.Package!, result.Manifest?.Version);
    }

    /// <summary>One-click flow: download (with a real progress bar) -> verify -> install.
    /// Verification is automatic; a declined elevation prompt keeps the package staged
    /// for a retry.</summary>
    private async Task RunUpdateAsync(UpdatePackage package, string? version)
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
            _stagedPackagePath = await _service.DownloadAndVerifyAsync(package, download, targetVersion: version);

            Progress.IsIndeterminate = true;
            StatusText.Text = "Verified. Installing...";
            await InstallStagedAsync();
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = ex.Message;
            InstallButton.Visibility = Visibility.Visible; // allow a retry
            DismissButton.Visibility = Visibility.Visible; // or give up on it
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
            StatusText.Text = _service.MsixInstallHandedOff
                ? "Installing update - CleanMachine will close and reopen shortly."
                : "Update installed. Please restart CleanMachine.";
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
