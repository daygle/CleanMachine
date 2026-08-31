namespace CleanMachine.Windows;

public sealed record CleanupProgress(string Phase, int Completed, int Total, long BytesProcessed);
public sealed record CleanupIssue(string Path, string Reason);
public sealed record CleanupReport(CleanupResult Result, IReadOnlyList<CleanupIssue> Skipped);
public sealed record UpdateState(string Status, string? PackagePath, string? RollbackPath, DateTimeOffset UpdatedAt);
