using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CleanMachine.Windows;

public sealed record RegistryReview(IReadOnlyList<RegistryFinding> Findings, RegistryBackup? Backup);

public sealed class RegistryCareService
{
    private readonly CleanupService _cleanup = new();

    public async Task<RegistryReview> ScanAsync(CancellationToken token = default)
        => new((await _cleanup.ScanRegistrySafelyAsync(token))
            .OrderByDescending(f => f.Confidence)
            .Take(250)
            .ToArray(), null);

    public async Task<RegistryReview> PrepareReviewAsync(
        IEnumerable<RegistryFinding> selected,
        CancellationToken token = default)
    {
        var safe = selected
            .Where(f => f.LowRisk && f.Confidence >= 70)
            .Take(250)
            .ToArray();
        return safe.Length == 0
            ? new RegistryReview([], null)
            : new RegistryReview(safe, await ExportCurrentUserUninstallKeyAsync(token));
    }

    public static async Task<bool> ValidateBackupAsync(
        RegistryBackup backup,
        CancellationToken token = default)
    {
        if (!File.Exists(backup.FilePath))
            return false;
        if (backup.FilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
        {
            var info = new FileInfo(backup.FilePath);
            if (info.Length == 0) return false;
            await using var stream = File.OpenRead(backup.FilePath);
            var header = new byte[10];
            var read = await stream.ReadAsync(header.AsMemory(), token);
            return read >= 5 && System.Text.Encoding.ASCII.GetString(header, 0, 5) == "REGED";
        }
        // .txt backups are also valid (text summary format)
        return new FileInfo(backup.FilePath).Length > 0;
    }

    public static async Task RestoreBackupAsync(
        RegistryBackup backup,
        CancellationToken token = default)
    {
        if (!await ValidateBackupAsync(backup, token))
            throw new InvalidDataException("The registry backup is missing, empty, or invalid.");
        if (!backup.FilePath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .reg file backups can be restored through the registry importer.");

        var psi = new ProcessStartInfo("reg.exe", $"import \"{backup.FilePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry restore tool.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Registry backup restore failed.");
    }

    private static async Task<RegistryBackup> ExportCurrentUserUninstallKeyAsync(CancellationToken token)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CleanMachine", "Backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"registry-review-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.reg");

        var psi = new ProcessStartInfo("reg.exe",
            $"export \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\" \"{path}\" /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows registry export tool.");
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException("Registry backup export failed.");

        return new RegistryBackup(path, DateTimeOffset.UtcNow);
    }
}
