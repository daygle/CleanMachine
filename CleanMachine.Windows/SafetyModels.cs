namespace CleanMachine.Windows;

public sealed record CleanupProgress(string Phase, int Completed, int Total, long BytesProcessed);
public sealed record CleanupIssue(string Path, string Reason);
/// <summary>Per-category (or per-item) contribution to a clean, used to build the
/// Activity page's drill-down breakdown.</summary>
public sealed record CleanupCategoryResult(string Category, int Removed, long Bytes);
public sealed record CleanupReport(CleanupResult Result, IReadOnlyList<CleanupIssue> Skipped, IReadOnlyList<CleanupCategoryResult>? Breakdown = null);
public sealed record UpdateState(string Status, string? PackagePath, string? RollbackPath, DateTimeOffset UpdatedAt);
