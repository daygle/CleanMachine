namespace CleanMachine.Windows;

public sealed record RegistryFindingDetails(RegistryFinding Finding, int Confidence, string Explanation);
public sealed record SecureDeleteSelection(string Path, long Bytes, bool Selected);
public sealed record ModuleProgress(string Module, string Status, int Completed, int Total, long Bytes);
