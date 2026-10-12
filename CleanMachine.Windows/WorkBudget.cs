using System.Diagnostics;

namespace CleanMachine.Windows;

/// <summary>Explicit fail-closed work limits. Exceeding a budget is reported,
/// never silently interpreted as a complete scan or successful cleanup.</summary>
internal sealed class WorkBudget(CancellationToken token = default, int maxEntries = 100000,
    int maxDepth = 64, long maxBytes = long.MaxValue)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _entries;
    private long _bytes;
    internal void Check(int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        if (++_entries > maxEntries || depth > maxDepth || _clock.Elapsed > TimeSpan.FromSeconds(30))
            throw new IOException("Cleanup work limit reached; narrow the selection and retry.");
    }

    internal void ConsumeBytes(long count)
    {
        if (count < 0 || count > maxBytes - _bytes)
            throw new IOException("Cleanup byte limit reached; narrow the selection and retry.");
        _bytes += count;
        Check();
    }
}
