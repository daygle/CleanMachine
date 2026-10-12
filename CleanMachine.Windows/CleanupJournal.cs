namespace CleanMachine.Windows;

/// <summary>Tracks successful mutations inside a service even when its normal
/// result never returns. Cancellation records partial totals with an independent
/// token before propagating. Normal results remain the caller's responsibility,
/// avoiding double-counting. Cancellation does not imply rollback.</summary>
internal static class CleanupJournal
{
    private sealed class State
    {
        internal long Items;
        internal long Bytes;
    }
    private static readonly AsyncLocal<State?> Current = new();

    internal static void Record(long items, long bytes)
    {
        if (Current.Value is not { } state) return;
        state.Items += items;
        state.Bytes += bytes;
    }

    internal static async Task<T> RunAsync<T>(string area, Func<Task<T>> action,
        Func<string, long, long, Task>? recordInterrupted = null)
    {
        var previous = Current.Value;
        var state = new State();
        Current.Value = state;
        try { return await action(); }
        catch (OperationCanceledException)
        {
            await (recordInterrupted ?? RecordInterruptedAsync)(area, state.Items, state.Bytes);
            throw;
        }
        finally { Current.Value = previous; }
    }

    internal static async Task RecordInterruptedAsync(string area, long items, long bytes)
    {
        await new CleanupStatsStore().RecordAsync(items, bytes, CancellationToken.None);
        await new ActivityStore().AddAsync(new ActivityEntry(DateTimeOffset.UtcNow,
            area + " Cancelled",
            $"Stopped after {items:N0} item(s), {AppNotifications.FormatBytes(bytes)} recovered. Completed changes were not rolled back."),
            CancellationToken.None);
    }
}
