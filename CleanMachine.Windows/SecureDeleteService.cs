using System.Security.Cryptography;

namespace CleanMachine.Windows;

public enum WipeMethod
{
    SimpleZeroFill,
    Dod522022M,
    Dod522022MEce,
    PeterGutmann,
    Custom
}

public sealed record SecureDeleteOptions(WipeMethod Method = WipeMethod.SimpleZeroFill, int CustomPasses = 1, bool ConfirmSolidStateDriveWarning = false)
{
    public int Passes => Method switch
    {
        WipeMethod.SimpleZeroFill => 1,
        WipeMethod.Dod522022M => 3,
        WipeMethod.Dod522022MEce => 7,
        WipeMethod.PeterGutmann => 35,
        WipeMethod.Custom => Math.Clamp(CustomPasses, 1, 35),
        _ => 1
    };
}

public sealed record SecureDeleteResult(int FilesProcessed, long BytesOverwritten, int FilesSkipped, WipeMethod Method);

public sealed class SecureDeleteService
{
    public async Task<SecureDeleteResult> DeleteAsync(IEnumerable<string> files, SecureDeleteOptions options, CancellationToken cancellationToken = default)
    {
        var processed = 0; var skipped = 0; long bytes = 0;
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.IsReadOnly || info.Length == 0 || (!options.ConfirmSolidStateDriveWarning && IsLikelySolidStateDrive(path))) { skipped++; continue; }
                await OverwriteAsync(path, info.Length, options, cancellationToken);
                File.Delete(path); processed++; bytes += info.Length;
            }
            catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; }
        }
        return new SecureDeleteResult(processed, bytes, skipped, options.Method);
    }

    private static async Task OverwriteAsync(string path, long length, SecureDeleteOptions options, CancellationToken token)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[Math.Min(bufferSize, Math.Max(1, length))];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous);
        for (var pass = 0; pass < options.Passes; pass++)
        {
            stream.Position = 0; long remaining = length;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, remaining);
                FillPattern(buffer, count, options.Method, pass);
                await stream.WriteAsync(buffer.AsMemory(0, count), token); remaining -= count;
            }
            await stream.FlushAsync(token);
        }
    }

    private static void FillPattern(byte[] buffer, int count, WipeMethod method, int pass)
    {
        if (method == WipeMethod.SimpleZeroFill || (method == WipeMethod.Dod522022M && pass == 2) || (method == WipeMethod.Dod522022MEce && pass is 2 or 5)) { Array.Clear(buffer, 0, count); return; }
        if (method == WipeMethod.Dod522022M || method == WipeMethod.Dod522022MEce) { buffer.AsSpan(0, count).Fill(pass % 2 == 0 ? (byte)0xFF : (byte)0x00); return; }
        RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
    }

    private static bool IsLikelySolidStateDrive(string path)
    {
        // Media detection is intentionally conservative until the Windows storage query
        // layer is connected. This prevents presenting overwrite as SSD sanitization.
        return true;
    }
}
