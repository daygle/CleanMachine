using System.Text.Json;

namespace CleanMachine.Windows;

/// <summary>One recorded cleanup run.</summary>
public sealed record CleanupRun(DateTimeOffset Time, long ItemsRemoved, long BytesRecovered);

public sealed record CleanupStatsFile(long ItemsRemoved, long BytesRecovered, DateTimeOffset LastCleanup, List<CleanupRun> Runs);

/// <summary>Persistent cleanup totals, shown on the Overview page. Holds lifetime
/// totals plus a capped list of individual runs so "recent" (30-day) totals can be
/// computed without parsing activity text. Every cleanup path (manual pages,
/// background agent, scheduled runs) records into this store; stats survive restarts.</summary>
public sealed class CleanupStatsStore
{
    private const int RecentWindowDays = 30;
    private const int MaxRuns = 200;
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CleanMachine", "stats.json");

    private static readonly CleanupStatsFile Empty = new(0, 0, DateTimeOffset.MinValue, []);

    // RecordAsync is a read-modify-write of stats.json and can be called
    // concurrently (background agent + UI runs); without this gate two overlapping
    // records would load the same baseline and one run's totals would be lost.
    private static readonly SemaphoreSlim RecordGate = new(1, 1);

    public async Task<CleanupStatsFile> LoadAsync(CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return Empty;
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<CleanupStatsFile>(stream, cancellationToken: token) ?? Empty;
        }
        catch (IOException) { return Empty; }
        catch (JsonException) { return Empty; }
    }

    /// <summary>Adds one cleanup run to the totals. Failures are swallowed:
    /// stats are best-effort and must never break a cleanup.</summary>
    public async Task RecordAsync(long itemsRemoved, long bytesRecovered, CancellationToken token = default)
    {
        if (itemsRemoved <= 0 && bytesRecovered <= 0) return;
        try
        {
            await RecordGate.WaitAsync(token);
            try
            {
                // Cleanup changed what could be cleaned: drop the Overview availability
                // cache so its cards never show pre-clean numbers. Best-effort, cheap.
                OverviewScanService.InvalidateCache();

                var current = await LoadAsync(token);
                var runs = current.Runs.Append(new CleanupRun(DateTimeOffset.UtcNow, Math.Max(itemsRemoved, 0), Math.Max(bytesRecovered, 0)))
                    .OrderByDescending(r => r.Time)
                    .Take(MaxRuns)
                    .ToList();
                var updated = new CleanupStatsFile(
                    current.ItemsRemoved + Math.Max(itemsRemoved, 0),
                    current.BytesRecovered + Math.Max(bytesRecovered, 0),
                    DateTimeOffset.UtcNow,
                    runs);
                await SaveAsync(updated, token);
            }
            finally { RecordGate.Release(); }
        }
        catch { /* stats are best-effort */ }
    }

    /// <summary>Items and bytes cleaned within the recent window of an already-loaded
    /// stats file, so callers that just loaded stats do not read the file twice.</summary>
    public static (long Items, long Bytes) RecentTotals(CleanupStatsFile stats)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RecentWindowDays);
        long items = 0, bytes = 0;
        foreach (var run in stats.Runs)
        {
            if (run.Time < cutoff) continue;
            items += run.ItemsRemoved;
            bytes += run.BytesRecovered;
        }
        return (items, bytes);
    }

    /// <summary>Items and bytes cleaned within the recent window.</summary>
    public static async Task<(long Items, long Bytes)> RecentTotalsAsync(CancellationToken token = default)
        => RecentTotals(await new CleanupStatsStore().LoadAsync(token));

    private static async Task SaveAsync(CleanupStatsFile stats, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, stats, new JsonSerializerOptions { WriteIndented = true }, token);
        File.Move(temp, FilePath, true);
    }
}
