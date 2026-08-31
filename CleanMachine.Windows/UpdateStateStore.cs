using System.Text.Json;

namespace CleanMachine.Windows;

public sealed class UpdateStateStore
{
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
        var directory = System.IO.Path.GetDirectoryName(PathName)!;
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, state with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken: token);
        File.Move(temp, PathName, true);
    }

    public async Task MarkAsync(string status, string? packagePath = null, string? rollbackPath = null, CancellationToken token = default)
        => await SaveAsync(new UpdateState(status, packagePath, rollbackPath, DateTimeOffset.UtcNow), token);

    public void Clear() { try { if (File.Exists(PathName)) File.Delete(PathName); } catch { } }

    public async Task<bool> HasPendingUpdateAsync(CancellationToken token = default)
        => (await LoadAsync(token)) is { Status: "staged" or "installing" };
}
