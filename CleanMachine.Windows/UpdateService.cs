using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Windows.ApplicationModel;

namespace CleanMachine.Windows;

public sealed record UpdatePackage(string PackageUrl, string Sha256, string Architecture, string? Publisher = null);
public sealed record UpdateManifest(string Version, string ReleaseNotes, string PackageUrl = "", string Sha256 = "", string Architecture = "", string? Publisher = null, Dictionary<string, UpdatePackage>? Packages = null, UpdatePackage? Installer = null);
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, UpdatePackage? Package, string? Error);

public sealed class UpdateService
{
    private static readonly Uri ManifestUri = new("https://github.com/daygle/CleanMachine/releases/latest/download/update-manifest.json");

    /// <summary>True when running inside an MSIX package; false for standalone .exe installs.</summary>
    public static bool IsInstalledAsMsix { get; } = TryGetIsMsix();

    /// <summary>True when Windows Smart App Control is in enforcement mode (HKLM
    /// CI policy state 1). SAC blocks executables without cloud-verified reputation
    /// from starting - including freshly downloaded, correctly signed installers -
    /// and offers no "Run anyway" override, which surfaces as a Win32Exception at
    /// Process.Start with a message the user cannot act on.</summary>
    public static bool IsSmartAppControlEnforcing { get; } = QuerySmartAppControlEnforcing();

    private static bool QuerySmartAppControlEnforcing()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
            return key?.GetValue("VerifiedAndReputablePolicyState") is int state && state == 1;
        }
        catch { return false; }
    }

    private static bool TryGetIsMsix()
    {
        try { _ = Package.Current.Id.FullName; return true; }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
    }
    // The published manifest is generated with camelCase keys (version, releaseNotes,
    // packages, ...). System.Text.Json is case-sensitive by default, so binding must
    // be case-insensitive for the record properties to populate.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UpdateStateStore _stateStore = new();

    /// <summary>Raised after a staged MSIX package has been handed to the update
    /// helper. The subscriber must exit the process promptly: the helper only
    /// deploys once every process of the package is gone.</summary>
    public static event Action? MsixHandoff;

    /// <summary>True when the last <see cref="InstallVerifiedPackageAsync"/> call
    /// handed the MSIX package to the detached helper (the app is about to exit),
    /// as opposed to deploying in-process.</summary>
    public bool MsixInstallHandedOff { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await _httpClient.GetStreamAsync(ManifestUri, cancellationToken);
            var manifest = await ParseManifestAsync(stream, cancellationToken);
            if (manifest is null || !Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.ReleaseNotes)) return new(false, null, null, "The release manifest was invalid.");
            var package = ResolvePackage(manifest);
            if (package is null || !IsValidPackage(package)) return new(false, null, null, "No signed update package is available for this device.");
            return new(IsNewer(manifest.Version), manifest, package, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(false, null, null, "Update check could not be completed."); }
    }

    internal static async Task<UpdateManifest?> ParseManifestAsync(Stream stream, CancellationToken cancellationToken = default)
        => await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, ManifestJsonOptions, cancellationToken: cancellationToken);

    /// <summary>Downloads the package, reporting download fraction (0..1) via
    /// <paramref name="progress"/>, then verifies it by SHA-256 (and MSIX publisher)
    /// before staging it. Verification is automatic - a failed hash/publisher check
    /// deletes the download and throws.</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdatePackage package, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? targetVersion = null)
    {
        if (!IsValidPackage(package)) throw new InvalidOperationException("No signed update package is available for this device.");
        var isMsix = package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var ext = isMsix ? ".msix" : ".exe";
        var directory = Path.Combine(Path.GetTempPath(), "CleanMachine", "Updates"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"CleanMachine-{DateTime.UtcNow:yyyyMMddHHmmss}-{package.Architecture}{ext}");
        try
        {
            // ResponseHeadersRead so we get Content-Length up front and can stream with
            // progress; the body read is not bound by HttpClient.Timeout.
            using (var response = await _httpClient.GetAsync(new Uri(package.PackageUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)copied / total.Value));
                }
            }
            progress?.Report(1.0);
            await using var verify = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded package failed hash verification.");
            if (!HasExpectedPublisher(path, package.Publisher)) throw new InvalidDataException("The downloaded update failed publisher verification.");
            await _stateStore.MarkAsync(
                "staged", path, null, cancellationToken,
                expectedSha256: package.Sha256,
                expectedPublisher: package.Publisher,
                targetVersion: targetVersion);
            await RecordUpdateActivityAsync(
                "Update Staged",
                $"A verified update package was staged and is ready to install: {Path.GetFileName(path)}.");
            return path;
        }
        catch { TryDelete(path); throw; }
    }

    public async Task InstallVerifiedPackageAsync(
        string packagePath,
        string currentExecutable,
        CancellationToken cancellationToken = default,
        bool automatic = false)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Staged package not found.", packagePath);
        var stagedState = await _stateStore.LoadAsync(cancellationToken);
        var expectedHash = stagedState?.ExpectedSha256;
        if (stagedState is not { Status: "staged" or "installing" }
            || !string.Equals(stagedState.PackagePath, packagePath, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(expectedHash)
            || expectedHash.Length != 64)
            throw new InvalidDataException("The staged update could not be authenticated.");

        await using (var verifyStream = File.OpenRead(packagePath))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged update changed after verification.");
        }

        var isMsix = packagePath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var isExe = packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isMsix && !isExe) throw new InvalidDataException("A verified MSIX or EXE package is required.");
        if (!HasExpectedPublisher(packagePath, stagedState.ExpectedPublisher))
            throw new InvalidDataException("The staged update failed publisher verification.");

        // Rollback only matters for .exe installs: MSIX binaries live under the
        // protected WindowsApps folder and cannot be restored by file replacement,
        // so no rollback copy is staged (an orphaned one is cleaned up at startup).
        var rollback = isMsix
            ? null
            : await StageRollbackCopyAsync(currentExecutable, cancellationToken);
        await _stateStore.MarkAsync("installing", packagePath, rollback, cancellationToken,
            source: automatic ? "automatic" : "manual");
        try
        {
            if (isMsix)
            {
                // Hand the package to a detached helper and exit instead of deploying
                // from inside the running app. Replacing the package while this
                // process - and its window - is still alive makes Windows freeze the
                // window and kill the app mid-deployment; WER files that as an AppHang
                // ("Stopped responding and was closed") on every MSIX update, which
                // the user experiences as a crash. The helper runs outside the package
                // via Task Scheduler, waits for this process to exit, installs, and
                // relaunches the app.
                if (MsixHandoff is not null
                    && ScheduleService.TryGetPackageFamilyName(out var family)
                    && family is not null
                    && await StartMsixUpdateHelperAsync(
                        packagePath, family, automatic, UpdateStateStore.ErrorPath, cancellationToken))
                {
                    MsixInstallHandedOff = true;
                    MsixHandoff.Invoke();
                    return;
                }
                // Helper unavailable. Deploying in-process is NOT a safe fallback,
                // and never was: PackageManager can only replace the package that owns
                // this process from the outside, so it must pass ForceApplicationShutdown,
                // which freezes the window and terminates the app mid-deployment. That
                // is precisely the AppHang ("Stopped responding and was closed") this
                // hand-off exists to prevent, and it is what the event log recorded on
                // every MSIX update before the helper was added - the user just sees the
                // app die. A visible, retryable failure beats a frozen app.
                //
                // Handle it exactly like the .exe branch's "nothing was installed"
                // cases: keep the package staged so a retry needs no re-download, and
                // report a clean cancellation so the outer handler does not demand a
                // rollback that has nothing to roll back. The Updates page shows the
                // message; the idle auto-installer leaves the update pending.
                await _stateStore.MarkAsync("staged", packagePath, null, cancellationToken);
                throw new OperationCanceledException(
                    "Windows would not start the background update helper, and installing the " +
                    "update from inside the running app would freeze and close CleanMachine. " +
                    "The verified update is still staged - install it again to retry.");
            }
            else
            {
                // .exe installer: launch silently and exit so the installer can replace files.
                // Inno Setup /SILENT shows a progress bar; /SUPPRESSMSGBOXES prevents dialogs;
                // /NORESTART avoids an automatic reboot. /relaunch=1 tells the installer to
                // reopen the app once files are in place - a silent install skips the
                // Finished-page launch, so without this the app would just close and stay
                // closed after updating.
                // Elevation is requested only for per-machine installs (Program Files);
                // a per-user install lives under %LOCALAPPDATA%\Programs, which the
                // user can already write, so it updates with no UAC prompt at all.
                var psi = new ProcessStartInfo(packagePath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1")
                {
                    UseShellExecute = true,
                    Verb = InstallNeedsElevation(currentExecutable) ? "runas" : "open"
                };
                try
                {
                    using var installer = Process.Start(psi)
                        ?? throw new InvalidOperationException("Could not start the update installer.");
                    await installer.WaitForExitAsync(cancellationToken);
                    if (installer.ExitCode != 0)
                        throw new InvalidOperationException($"The update installer returned exit code {installer.ExitCode}.");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // ERROR_CANCELLED: the user declined the UAC prompt. Nothing was
                    // installed, so keep the package staged for a retry and report a
                    // clean cancellation instead of a failure needing rollback.
                    await _stateStore.MarkAsync("staged", packagePath, null, cancellationToken);
                    throw new OperationCanceledException("Administrator permission is required to install the update.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Windows refused to start the downloaded installer. The dominant
                    // cause is Smart App Control enforcement: SAC blocks any download
                    // without cloud reputation (valid signature or not) and, unlike
                    // SmartScreen, offers no "Run anyway" button. Give the user the
                    // actual reason and an actionable next step instead of the raw
                    // Win32 error text. Nothing was installed, so - like a declined
                    // UAC prompt - keep the package staged (no rollback) and report a
                    // cancellation so the retry path stays available.
                    await _stateStore.MarkAsync("staged", packagePath, null, cancellationToken);
                    var reason = IsSmartAppControlEnforcing
                        ? "Smart App Control is ON in Windows Security and has not built a trust reputation for this download yet (new certificates earn it over time). To update now, download the installer from the releases page, allow it in your security software's protection history if it was blocked, or turn Smart App Control off in Windows Security > App & browser control (turning it off is permanent without resetting Windows)."
                        : "Your antivirus or security software may have quarantined the downloaded installer, or the download is damaged. Check your antivirus protection history, then retry the update or install manually from the releases page.";
                    throw new OperationCanceledException($"Windows refused to start the update installer (error {ex.NativeErrorCode}). {reason}", ex);
                }
            }
            await _stateStore.MarkAsync("installed", packagePath, rollback, cancellationToken);
            await RecordUpdateActivityAsync(
                automatic ? "Automatic Update" : "Manual Update",
                $"{(automatic ? "Automatic" : "Manual")} update installed successfully: {Path.GetFileName(packagePath)}.");
            CleanupRollbackCopy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _stateStore.MarkAsync("rollback-required", packagePath, rollback, cancellationToken);
            throw;
        }
    }

    /// <summary>Builds the PowerShell script for the MSIX update helper. The helper
    /// waits until every CleanMachine process is gone (the handing-off app exits right
    /// after starting it), installs the package - retrying, because the first attempt
    /// often races the exiting process - then relaunches the app: the new version when
    /// the install landed, otherwise the one still installed, so the user is never left
    /// without a running app. A failed install is written to <paramref name="errorPath"/>
    /// so the Updates page can explain what went wrong on the next launch instead of
    /// silently re-offering the same update. Pure function: the round-trip through
    /// -EncodedCommand is covered by unit tests.</summary>
    internal static string BuildMsixUpdateScript(
        string packagePath,
        string familyName,
        bool relaunchBackground,
        string? errorPath = null)
    {
        static string Q(string value) => value.Replace("'", "''");
        var processName = typeof(UpdateService).Assembly.GetName().Name ?? "CleanMachine";
        return
            $"$pkg='{Q(packagePath)}';" +
            $"$fam='{Q(familyName)}';" +
            $"$name='{Q(processName)}';" +
            $"$bg={(relaunchBackground ? "$true" : "$false")};" +
            (errorPath is null ? "" : $"$errFile='{Q(errorPath)}';") +
            // The task definition outlives this run on purpose (deleting a task that is
            // still executing terminates it mid-deployment). If that leftover task ever
            // fires again - e.g. the PC was off when its scheduled time passed - the
            // staged package is long gone, so exit without deploying or relaunching.
            "if(-not (Test-Path -LiteralPath $pkg)){ exit 0 };" +
            "$deadline=[DateTimeOffset]::UtcNow.AddSeconds(60);" +
            // Never wait forever: if the app refuses to exit, the deploy below fails
            // harmlessly (package in use) and the relaunch brings the running copy up.
            "while([DateTimeOffset]::UtcNow -lt $deadline -and (Get-Process -Name $name -ErrorAction SilentlyContinue)){Start-Sleep -Milliseconds 400};" +
            // Clear any report from a previous attempt before trying, so a later
            // success never leaves a stale failure message behind.
            (errorPath is null ? "" : "if($errFile){ try{ Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue }catch{ } };") +
            // Retry the deploy several times. The first attempt routinely loses the
            // race with the just-exited process (the deployment service still holds
            // the old package files for a moment), and a single silent failure was
            // what users saw as "the update did nothing": the old version came back.
            "$err='';" +
            "for($i=0;$i -lt 5;$i++){ try{ Add-AppxPackage -Path $pkg -ErrorAction Stop; $err=''; break }catch{ $err=$_.Exception.Message; Start-Sleep -Seconds 2 } };" +
            // Record the failure as plain text where the Updates page reads it on the
            // next launch, so the user learns what went wrong instead of being offered
            // the same update again with no explanation.
            (errorPath is null ? "" :
                "if($err -ne '' -and $errFile){ try{ Set-Content -LiteralPath $errFile -Value $err -Encoding UTF8 -ErrorAction Stop }catch{ } };") +
            "$tail=''; if($bg){ $tail=' --background' };" +
            "& cmd.exe /c ('start \"\" \"shell:AppsFolder\\' + $fam + '!App\"' + $tail)";
    }

    /// <summary>Builds the powershell.exe command line for the helper task. The script
    /// travels base64-encoded (-EncodedCommand) so package paths, quotes and the
    /// shell: URI survive schtasks' quoting layers untouched.</summary>
    internal static string BuildMsixUpdateHelperCommand(string script)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return $"\"{powerShell}\" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";
    }

    /// <summary>Creates, fires, and removes the one-shot Task Scheduler task that runs
    /// the update helper. Launching through the scheduler instead of as a child process
    /// is deliberate: a child of this app would inherit the package identity and then
    /// be unable to Add-AppxPackage over the very package it belongs to, while a
    /// scheduler-launched process has a plain user token. Returns false when the task
    /// could not be started, so the caller can fall back to in-process deployment.</summary>
    internal static async Task<bool> StartMsixUpdateHelperAsync(
        string packagePath,
        string familyName,
        bool relaunchBackground,
        string? errorPath = null,
        CancellationToken token = default)
    {
        var script = BuildMsixUpdateScript(packagePath, familyName, relaunchBackground, errorPath);
        var command = BuildMsixUpdateHelperCommand(script).Replace("\"", "\\\"");
        // /ST only has to be a valid future time: /Run fires the task immediately
        // regardless of the schedule. The definition is deliberately NOT deleted
        // after /Run: deleting a scheduled task that is still executing terminates
        // it, which is exactly how the update ended up neither applied nor reported
        // (the app had already exited, so the user was left with nothing running).
        // The next update re-creates the definition with /F, and if this leftover
        // one ever fires again the script exits immediately on the missing package.
        var created = await ScheduleService.RunProcessAsync("schtasks.exe",
            $"/Create /TN \"{ScheduledTask.UpdateHelperTaskName}\" /TR \"{command}\" " +
            $"/SC ONCE /ST {DateTime.Now.AddMinutes(2):HH:mm} /RL LIMITED /F", token);
        if (!created) return false;
        return await ScheduleService.RunProcessAsync("schtasks.exe",
            $"/Run /TN \"{ScheduledTask.UpdateHelperTaskName}\"", token);
    }

    /// <summary>True when replacing the running executable needs an administrator,
    /// i.e. the install lives in a directory a standard user cannot write (a
    /// per-machine install under Program Files). A per-user install under
    /// %LOCALAPPDATA%\Programs is writable by its owner, so the silent installer
    /// (PrivilegesRequired=lowest) needs no elevation and no UAC prompt appears.
    /// Probing real writability with a self-deleting file also makes an
    /// already-elevated process report false. Any unexpected probe failure
    /// defaults to true: an unnecessary UAC prompt is better than an installer
    /// that silently cannot write its files.</summary>
    internal static bool InstallNeedsElevation(string currentExecutable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(currentExecutable));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return true;
        try
        {
            using (File.Create(Path.Combine(directory, ".update-write-probe"), 1, FileOptions.DeleteOnClose)) { }
            return false;
        }
        catch { return true; }
    }

    /// <summary>Deletes update leftovers from the install directory: rollback
    /// staging copies (<c>.restore</c>, <c>.failed</c>) whose end-of-rollback
    /// delete failed while a scanner still held the file, and the elevation
    /// write probe. Inno's uninstaller cannot remove files it did not install,
    /// so orphans here would keep the install folder alive after uninstall.
    /// The staging copies are always garbage once RollbackAsync has finished or
    /// died - the master rollback copy lives under %LOCALAPPDATA%. Best-effort
    /// per file; called once at startup before any update flow can run.</summary>
    internal static void CleanupUpdateArtifacts(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory)) return;
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(installDirectory, "*.restore")
                .Concat(Directory.GetFiles(installDirectory, "*.failed"))
                .Append(Path.Combine(installDirectory, ".update-write-probe"))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in candidates)
            TryDelete(file);
    }

    public async Task<bool> RollbackAsync(string currentExecutable, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadAsync(cancellationToken);
        var rollback = state?.RollbackPath ?? FindRollbackCopy();
        if (string.IsNullOrWhiteSpace(rollback) || !File.Exists(rollback) || !File.Exists(currentExecutable)) return false;
        // Stage the restored copy before touching the live executable so a mid-restore
        // failure never leaves the application without a runnable binary.
        var staged = currentExecutable + ".restore";
        var backup = currentExecutable + ".failed";
        try
        {
            File.Copy(rollback, staged, true);
            File.Replace(staged, currentExecutable, backup, ignoreMetadataErrors: true);
            TryDelete(backup);
        }
        catch { TryDelete(staged); throw; }
        await _stateStore.MarkAsync("rolled-back", null, rollback, cancellationToken);
        return true;
    }

    public Task<string> StageRollbackCopyAsync(string currentExecutable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(currentExecutable))
            throw new FileNotFoundException("Current application was not found.", currentExecutable);
        var directory = Path.Combine(
            AppDataPaths.Root, "Updates", "rollback");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "CleanMachine.previous");
        File.Copy(currentExecutable, copy, true);
        return Task.FromResult(copy);
    }

    public static string? FindRollbackCopy()
    {
        var copy = Path.Combine(
            AppDataPaths.Root, "Updates", "rollback", "CleanMachine.previous");
        return File.Exists(copy) ? copy : null;
    }

    public static void CleanupRollbackCopy()
    {
        var copy = FindRollbackCopy();
        try { if (copy is not null) File.Delete(copy); } catch { }
    }
    private static UpdatePackage? ResolvePackage(UpdateManifest manifest)
    {
        var arch = CurrentArchitecture();
        // .exe installs use the standalone installer; MSIX installs use the per-architecture package.
        if (!IsInstalledAsMsix)
        {
            if (manifest.Installer is not null && manifest.Installer.Architecture == arch) return manifest.Installer;
            return null; // No installer available for this architecture.
        }
        if (manifest.Packages is not null && manifest.Packages.TryGetValue(arch, out var package)) return package;
        return manifest.Architecture == arch && !string.IsNullOrWhiteSpace(manifest.PackageUrl) ? new UpdatePackage(manifest.PackageUrl, manifest.Sha256, manifest.Architecture, manifest.Publisher) : null;
    }
    private static bool IsValidPackage(UpdatePackage? package)
    {
        // JSON is external input; nullable/missing fields must be rejected rather
        // than reaching string members and turning a malformed manifest into an
        // unhandled exception in the update-check path.
        if (package is null
            || string.IsNullOrWhiteSpace(package.PackageUrl)
            || string.IsNullOrWhiteSpace(package.Sha256)
            || !Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit)) return false;
        if (package.Architecture != CurrentArchitecture()) return false;
        var isMsix = package.PackageUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
        var isExe = package.PackageUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isMsix && !isExe) return false;
        // Both package types must identify the expected release publisher. Hashes
        // provide integrity, while the publisher check prevents a validly hashed
        // but unsigned/re-signed package from entering the install path.
        if (string.IsNullOrWhiteSpace(package.Publisher)) return false;
        return true;
    }
    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : "x86";
    internal static bool IsNewer(string version) => Version.TryParse(version, out var candidate) && candidate > CurrentVersion();

    /// <summary>The running app's version: the MSIX package identity when packaged,
    /// otherwise the assembly version stamped at build time from the release tag
    /// (the installer build carries no package identity, so Package.Current throws).</summary>
    internal static Version CurrentVersion()
    {
        try { return new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            var assemblyVersion = typeof(UpdateService).Assembly.GetName().Version;
            return assemblyVersion is null ? new(0, 0, 0, 0) : new(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build, 0);
        }
    }
    private static bool HasExpectedPublisher(string path, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        // CreateFromSignedFile extracts the Authenticode signer certificate from a
        // signed package (exe/msix). SYSLIB0057 suggests X509CertificateLoader, but
        // that loads standalone certificate files and cannot read a signed package,
        // so switching would reject every valid MSIX update. The API remains
        // functional in .NET 10; revisit if it is ever removed.
#pragma warning disable SYSLIB0057
        try { using var cert = X509Certificate.CreateFromSignedFile(path); return cert.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
#pragma warning restore SYSLIB0057
    }
    private static async Task RecordUpdateActivityAsync(string title, string detail)
    {
        try
        {
            await new ActivityStore().AddAsync(new ActivityEntry(DateTimeOffset.UtcNow, title, detail));
        }
        catch
        {
            // Activity history is diagnostic; update completion must not be reported
            // as failed when the history file cannot be written.
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
