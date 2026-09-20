namespace CleanMachine.Windows;

/// <summary>Silently downloads, verifies, and installs an update found by an
/// automatic check, waiting until the machine is idle. Used by the
/// "install updates automatically when idle" setting.</summary>
/// <remarks>
/// Safety gates, re-checked immediately before anything irreversible:
/// - Only silent-capable installs qualify: per-user installs whose directory is
///   writable. A per-machine install would pop a UAC prompt while the user is
///   away - the exact opposite of silent - so it is never auto-installed.
/// - The install waits for a real idle window (no keyboard/mouse input for
///   <see cref="MinimumIdle"/>) and abandons the attempt the moment the user
///   returns; a failed gate simply waits for the next automatic check.
/// - Secure Delete and Drive Wiper runs block it via
///   <see cref="IsDestructiveOperationRunning"/> so an update restart can never
///   interrupt a destructive operation.
/// - MSIX updates install through PackageManager with no elevation and qualify;
///   the writability probe already covers the .exe installer path.
/// One attempt runs at a time (interlocked) and each package is attempted at
/// most once per app session, so a failing package cannot loop.
/// </remarks>
public sealed class UpdateAutoInstaller
{
    /// <summary>Minimum idle time before a silent install may start. Long enough
    /// that stepping away for coffee never triggers a restart on return, short
    /// enough that a machine left on overnight gets updated.</summary>
    public static readonly TimeSpan MinimumIdle = TimeSpan.FromMinutes(30);

    /// <summary>Set while Secure Delete or Drive Wiper is running; an update
    /// never restarts the app mid-wipe.</summary>
    private static int _destructiveOperationRunning;
    public static bool IsDestructiveOperationRunning
    {
        get => Volatile.Read(ref _destructiveOperationRunning) != 0;
        set => Volatile.Write(ref _destructiveOperationRunning, value ? 1 : 0);
    }

    private static int _running;
    private static readonly HashSet<string> _attemptedPackages = new(StringComparer.OrdinalIgnoreCase);

    private readonly UpdateService _service = new();

    /// <summary>Attempts the full silent update for the given automatic-check
    /// result. The returned task never throws; every failure is reflected in the
    /// Activity log. A cancellation token stops the wait when the background agent
    /// is disabled or the app exits.</summary>
    public async Task TryInstallWhenIdleAsync(UpdateCheckResult result, CancellationToken cancellationToken = default)
    {
        if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var package = result.Package;
            var executable = Environment.ProcessPath;
            if (!result.Available || package is null || executable is null) return;
            // Never auto-install what needs an elevation prompt, and only one
            // attempt per package per session.
            if (UpdateService.InstallNeedsElevation(executable)) return;
            if (!_attemptedPackages.Add(package.Sha256)) return;

            AppNotifications.ShowUpdateScheduled(result.Manifest?.Version ?? "a new version");

            // Wait for a true idle window with nothing destructive running.
            // An initial 30-minute delay doubles as the first idle check; the
            // loop re-checks every minute and gives up after a day (the next
            // automatic check, 6h after this one, starts fresh).
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
            await Task.Delay(MinimumIdle, cancellationToken);
            while (App.IdleTime() < MinimumIdle || IsDestructiveOperationRunning)
            {
                if (DateTimeOffset.UtcNow > deadline) return;
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }

            // Download and verify only now, so a cancelled/failed idle wait never
            // left a large temp download sitting around for hours.
            var path = await _service.DownloadAndVerifyAsync(package, new Progress<double>(), cancellationToken, result.Manifest?.Version);
            // The user may have returned during the download; the package stays
            // staged either way, so the manual flow can install without a
            // re-download.
            if (App.IdleTime() < MinimumIdle || IsDestructiveOperationRunning) return;

            await _service.InstallVerifiedPackageAsync(path, executable, cancellationToken, automatic: true);
        }
        catch (OperationCanceledException)
        {
            // UAC declined (should not happen for silent-capable installs, but a
            // policy change mid-session could), SAC refusal, or a network error:
            // the package stays staged for the manual retry path.
        }
        catch (Exception ex)
        {
            try
            {
                await new ActivityStore().AddAsync(new ActivityEntry(
                    DateTimeOffset.UtcNow,
                    "Automatic Update",
                    $"Automatic update install failed: {ex.Message}"));
            }
            catch { /* activity store is best-effort */ }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _running, 0);
        }
    }
}
