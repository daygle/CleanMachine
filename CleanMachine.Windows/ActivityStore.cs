using System.Text.Json;

namespace CleanMachine.Windows;

public sealed record ActivityEntry(DateTimeOffset Time, string Title, string Detail);
public sealed class ActivityStore
{
    private const int MaxEntries = 100;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "activity.json");
    public async Task<IReadOnlyList<ActivityEntry>> LoadAsync(CancellationToken token = default)
    {
        try { if (!File.Exists(FilePath)) return []; await using var stream = File.OpenRead(FilePath); return await JsonSerializer.DeserializeAsync<List<ActivityEntry>>(stream, cancellationToken: token) ?? []; } catch (IOException) { return []; } catch (JsonException) { return []; }
    }
    public async Task AddAsync(ActivityEntry entry, CancellationToken token = default)
    {
        var items = (await LoadAsync(token)).Prepend(entry).Take(MaxEntries).ToList(); await SaveAsync(items, token);
    }
    public Task ClearAsync(CancellationToken token = default) => SaveAsync([], token);
    private static async Task SaveAsync(IReadOnlyList<ActivityEntry> items, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory); var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, items, new JsonSerializerOptions { WriteIndented = true }, token);
        File.Move(temp, FilePath, true);
    }
}
