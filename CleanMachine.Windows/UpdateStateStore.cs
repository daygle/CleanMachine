using System.Text.Json;

namespace CleanMachine.Windows;

public sealed class UpdateStateStore
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static string PathName => Path.Combine(AppDataPaths.Root, "Updates", "state.json");

    /// <summary>Where the MSIX update helper (a PowerShell process outside the
    /// package) drops the reason an install failed. It is plain text written with
    /// Set-Content, so the helper never has to hand-serialize the app's JSON state
    /// file; the Updates page reads it on the next launch and then deletes it.</summary>
    internal static string ErrorPath => Path.Combine(AppDataPaths.Root, "Updates", "install-error.txt");

    /// <summary>Reads and clears the helper's failure note. Returns null when the
    /// last update installed cleanly (or never ran).</summary>
    internal static string? TakeInstallError()
    {
        try
        {
            if (!File.Exists(ErrorPath)) return null;
            var text = File.ReadAllText(ErrorPath).Trim();
            TryDelete(ErrorPath);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public async Task<UpdateState?> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            await using var stream = File.OpenRead(PathName);
            return await JsonSerializer.DeserializeAsync<UpdateState>(stream, cancellationToken: token);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task SaveAsync(UpdateState state, CancellationToken token = default)
    {
        await SaveGate.WaitAsync(token);
        try { await SaveCoreAsync(state, token); }
        finally { SaveGate.Release(); }
    }

    public async Task MarkAsync(
        string status,
        string? packagePath = null,
        string? rollbackPath = null,
        CancellationToken token = default,
        string? expectedSha256 = null,
        string? expectedPublisher = null,
        string? targetVersion = null,
        string? source = null)
    {
        // The read must be inside the same critical section as the write. Otherwise
        // two transitions can both read the same old state and the later write can
        // restore stale package metadata or an older status.
        await SaveGate.WaitAsync(token);
        try
        {
            var current = await LoadAsync(token);
            await SaveCoreAsync(new UpdateState(
                status,
                packagePath,
                rollbackPath,
                DateTimeOffset.UtcNow,
                expectedSha256 ?? current?.ExpectedSha256,
                expectedPublisher ?? current?.ExpectedPublisher,
                targetVersion ?? current?.TargetVersion,
                source ?? current?.Source), token);
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>Removes any pending-update state and deletes the staged package
    /// file, so an update that was installed outside the app (or was interrupted
    /// and later superseded) stops being offered. Best-effort per file.</summary>
    public async Task DismissAsync(string? packagePath, CancellationToken token = default)
    {
        await SaveGate.WaitAsync(token);
        try
        {
            if (File.Exists(PathName)) File.Delete(PathName);
            if (!string.IsNullOrWhiteSpace(packagePath))
            {
                try { if (File.Exists(packagePath)) File.Delete(packagePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally { SaveGate.Release(); }
    }

    public void Clear()
    {
        SaveGate.Wait();
        try { if (File.Exists(PathName)) File.Delete(PathName); }
        catch { }
        finally { SaveGate.Release(); }
    }

    public async Task<bool> HasPendingUpdateAsync(CancellationToken token = default)
        => (await LoadAsync(token)) is { Status: "staged" or "installing" };

    /// <summary>Outcome of reconciling leftover update state. <see cref="Pending"/>
    /// is non-null only when an update is genuinely still installable; the flags let
    /// callers report or simply proceed.</summary>
    internal readonly record struct UpdateReconciliation(
        UpdateState? Pending,
        bool InstallRecorded,
        bool Dismissed);

    /// <summary>Retires update state left behind by a previous session.
    ///
    /// An MSIX install terminates the app mid-deployment, so the process that
    /// started it never records its own outcome. Without this pass the app carries
    /// a "pending" update it has already installed - and worse, it carried that lie
    /// everywhere except the Updates page, which is the only place this used to run.
    /// It now runs at startup too, so the Overview page never offers an update that
    /// is already installed.
    ///
    /// Two cases are resolved: an "installing" state whose target version is already
    /// running means the deployment actually landed, so the success is recorded; and
    /// a staged package that is gone, or that targets a version already running, is
    /// stale and gets dismissed. Best-effort throughout - a failure here must never
    /// stop the app from starting.</summary>
    internal static async Task<UpdateReconciliation> ReconcileAsync(CancellationToken token = default)
    {
        try
        {
            var store = new UpdateStateStore();
            var state = await store.LoadAsync(token);
            if (state is not { Status: "staged" or "installing" }
                || string.IsNullOrEmpty(state.PackagePath))
                return new UpdateReconciliation(null, false, false);

            var targetAtOrBelowCurrent = state.TargetVersion is not null
                && Version.TryParse(state.TargetVersion, out var target)
                && target <= UpdateService.CurrentVersion();

            var recorded = state.Status == "installing" && targetAtOrBelowCurrent;
            if (recorded) await RecordCompletedInstallAsync(state);

            var stale = !File.Exists(state.PackagePath) || targetAtOrBelowCurrent;
            if (!stale) return new UpdateReconciliation(state, recorded, false);

            await store.DismissAsync(state.PackagePath);
            return new UpdateReconciliation(null, recorded, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let leftover bookkeeping stop the app from launching.
            return new UpdateReconciliation(null, false, false);
        }
    }

    /// <summary>Records the success of an update whose process was terminated
    /// mid-deployment (MSIX ForceApplicationShutdown) before it could log its own
    /// Activity entry. Best-effort: history is diagnostic, never authoritative.</summary>
    private static async Task RecordCompletedInstallAsync(UpdateState state)
    {
        try
        {
            await new ActivityStore().AddAsync(new ActivityEntry(
                DateTimeOffset.UtcNow,
                state.Source == "automatic" ? "Automatic Update" : "Manual Update",
                $"Update installed successfully: {Path.GetFileName(state.PackagePath)}."));
        }
        catch { /* activity history is best-effort */ }
        UpdateService.CleanupRollbackCopy();
    }

    private static async Task SaveCoreAsync(UpdateState state, CancellationToken token)
    {
        var temp = $"{PathName}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(PathName)!;
            Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(
                    stream,
                    state with { UpdatedAt = DateTimeOffset.UtcNow },
                    cancellationToken: token);
            File.Move(temp, PathName, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
