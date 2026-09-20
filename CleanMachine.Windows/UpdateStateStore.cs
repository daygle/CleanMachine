using System.Text.Json;

namespace CleanMachine.Windows;

public sealed class UpdateStateStore
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "Updates", "state.json");

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
        string? targetVersion = null)
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
                targetVersion ?? current?.TargetVersion), token);
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
