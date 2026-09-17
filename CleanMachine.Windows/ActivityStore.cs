using System.Text.Json;

namespace CleanMachine.Windows;

/// <summary>One activity log entry. <see cref="Details"/> is an optional per-category
/// breakdown (e.g. "Temporary Files - 812 files, 140.2 MB") shown when the card is
/// expanded on the Activity page; null/empty for events with no drill-down.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, string Title, string Detail, IReadOnlyList<string>? Details = null);
public sealed class ActivityStore
{
    private const int MaxEntries = 100;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanMachine", "activity.json");

    // AddAsync is a read-modify-write and can be called concurrently (background
    // agent, scheduled runs, quick cleans); serialize it so entries are not lost.
    private static readonly SemaphoreSlim AddGate = new(1, 1);

    public async Task<IReadOnlyList<ActivityEntry>> LoadAsync(CancellationToken token = default)
    {
        try { if (!File.Exists(FilePath)) return []; await using var stream = File.OpenRead(FilePath); return await JsonSerializer.DeserializeAsync<List<ActivityEntry>>(stream, cancellationToken: token) ?? []; } catch (IOException) { return []; } catch (JsonException) { return []; }
    }
    public async Task AddAsync(ActivityEntry entry, CancellationToken token = default)
    {
        await AddGate.WaitAsync(token);
        try
        {
            var items = (await LoadAsync(token)).Prepend(entry).Take(MaxEntries).ToList(); await SaveAsync(items, token);
        }
        finally { AddGate.Release(); }
    }
    public Task ClearAsync(CancellationToken token = default) => SaveAsync([], token);

    /// <summary>Turns a clean's per-category breakdown into human-readable drill-down
    /// lines for an <see cref="ActivityEntry"/>, largest first. Returns null when there
    /// is nothing to show, so the Activity card stays un-expandable.</summary>
    public static IReadOnlyList<string>? BreakdownLines(IReadOnlyList<CleanupCategoryResult>? breakdown)
        => breakdown is { Count: > 0 }
            ? breakdown
                .OrderByDescending(b => b.Bytes).ThenByDescending(b => b.Removed)
                .Select(b => b.Bytes > 0
                    ? $"{b.Category} - {b.Removed:N0} item(s), {AppNotifications.FormatBytes(b.Bytes)}"
                    : $"{b.Category} - {b.Removed:N0} item(s)")
                .ToList()
            : null;
    private static async Task SaveAsync(IReadOnlyList<ActivityEntry> items, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory); var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, items, new JsonSerializerOptions { WriteIndented = true }, token);
        File.Move(temp, FilePath, true);
    }
}
