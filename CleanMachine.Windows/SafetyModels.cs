namespace CleanMachine.Windows;

public sealed record CleanupProgress(string Phase, int Completed, int Total, long BytesProcessed);
public sealed record CleanupIssue(string Path, string Reason);

/// <summary>Serializes destructive operations across manual, scheduled, and
/// background cleanup paths. Scanning may remain concurrent, but only one operation
/// may mutate files, registry state, recycle-bin contents, or free space at a time.</summary>
internal static class CleanupCoordinator
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
/// <summary>Per-category (or per-item) contribution to a clean, used to build the
/// Activity page's drill-down breakdown.</summary>
public sealed record CleanupCategoryResult(string Category, int Removed, long Bytes);
public sealed record CleanupReport(
    CleanupResult Result,
    IReadOnlyList<CleanupIssue> Skipped,
    IReadOnlyList<CleanupCategoryResult>? Breakdown = null,
    IReadOnlySet<string>? CleanedPaths = null);
