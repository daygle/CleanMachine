using System.Text.Json;

namespace CleanMachine.Windows;

public sealed record ActivityEntry(DateTimeOffset Time, string Title, string Detail, string Tone = "green");

public sealed class ActivityStore
{
    private const int MaximumEntries = 100;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "activity.json");

    public async Task<IReadOnlyList<ActivityEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<List<ActivityEntry>>(stream, cancellationToken: cancellationToken) ?? [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }

    public async Task AddAsync(ActivityEntry entry, CancellationToken cancellationToken = default)
    {
        var entries = (await LoadAsync(cancellationToken)).Prepend(entry).Take(MaximumEntries).ToList();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, entries, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        File.Move(temporary, FilePath, true);
    }
}
