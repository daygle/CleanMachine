using System.Security.Cryptography;

namespace CleanMachine.Windows;

public enum WipeMethod { SimpleZeroFill, Dod522022M, Dod522022MEce, PeterGutmann, Custom }
public sealed record SecureDeleteOptions(WipeMethod Method = WipeMethod.SimpleZeroFill, int CustomPasses = 1, bool ConfirmSolidStateDriveWarning = false)
{
    public int Passes => Method switch { WipeMethod.SimpleZeroFill => 1, WipeMethod.Dod522022M => 3, WipeMethod.Dod522022MEce => 7, WipeMethod.PeterGutmann => 35, WipeMethod.Custom => Math.Clamp(CustomPasses, 1, 35), _ => 1 };
}
public sealed record SecureDeleteResult(int FilesProcessed, long BytesOverwritten, int FilesSkipped, WipeMethod Method, IReadOnlyList<CleanupIssue> Issues);

public sealed class SecureDeleteService
{
    public Task<IReadOnlyList<SecureDeleteSelection>> PrepareSelectionAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        var result = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Select(path => { var info = new FileInfo(path); return new SecureDeleteSelection(path, info.Length, !info.IsReadOnly && !NativeSafety.IsProtectedPath(path)); }).ToArray(); return Task.FromResult<IReadOnlyList<SecureDeleteSelection>>(result);
    }
    public async Task<SecureDeleteResult> DeleteAsync(IEnumerable<SecureDeleteSelection> selection, SecureDeleteOptions options, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!options.ConfirmSolidStateDriveWarning) throw new InvalidOperationException("SSD overwrite acknowledgement is required.");
        var files = selection.Where(x => x.Selected).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var processed = 0; var skipped = 0; long bytes = 0; var issues = new List<CleanupIssue>();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = files[index]; try { var info = new FileInfo(path); if (!info.Exists || info.IsReadOnly || info.Length == 0 || NativeSafety.IsProtectedPath(path)) { skipped++; issues.Add(new(path, "File is empty, read-only, protected, or missing")); continue; } var originalLength = info.Length; await OverwriteAsync(path, originalLength, options, cancellationToken); if (new FileInfo(path).Length != originalLength) throw new IOException("File size changed during overwrite."); File.Delete(path); processed++; bytes += originalLength; } catch (IOException ex) { skipped++; issues.Add(new(path, ex.Message)); } catch (UnauthorizedAccessException) { skipped++; issues.Add(new(path, "Access denied")); } progress?.Report(new CleanupProgress("Secure Delete", index + 1, files.Length, bytes));
        }
        return new SecureDeleteResult(processed, bytes, skipped, options.Method, issues);
    }
    public Task<SecureDeleteResult> DeleteAsync(IEnumerable<string> files, SecureDeleteOptions options, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default) => DeleteAsync(files.Select(path => new SecureDeleteSelection(path, 0, true)), options, progress, cancellationToken);
    private static async Task OverwriteAsync(string path, long length, SecureDeleteOptions options, CancellationToken token) { const int bufferSize = 1024 * 1024; var buffer = new byte[Math.Min(bufferSize, Math.Max(1, length))]; await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous); for (var pass = 0; pass < options.Passes; pass++) { stream.Position = 0; long remaining = length; while (remaining > 0) { token.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, remaining); FillPattern(buffer, count, options.Method, pass); await stream.WriteAsync(buffer.AsMemory(0, count), token); remaining -= count; } await stream.FlushAsync(token); } }
    private static void FillPattern(byte[] buffer, int count, WipeMethod method, int pass) { if (method == WipeMethod.SimpleZeroFill || (method == WipeMethod.Dod522022M && pass == 2) || (method == WipeMethod.Dod522022MEce && pass is 2 or 5)) { Array.Clear(buffer, 0, count); return; } if (method is WipeMethod.Dod522022M or WipeMethod.Dod522022MEce) { buffer.AsSpan(0, count).Fill(pass % 2 == 0 ? (byte)0xFF : (byte)0x00); return; } RandomNumberGenerator.Fill(buffer.AsSpan(0, count)); }
}
