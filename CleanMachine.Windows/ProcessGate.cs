using System.Diagnostics;

namespace CleanMachine.Windows;

/// <summary>Async-compatible, crash-released per-user interprocess exclusion.
/// The lock file is never deleted: unlinking it could create two different locks.
/// Files live outside package virtualization and outside the deletable data tree.
/// FileShare.None is enforced by .NET on Windows and Unix; no thread-affine mutex
/// is held over an await. Access errors fail closed instead of disabling locking.</summary>
internal sealed class ProcessGate
{
    private readonly SemaphoreSlim _local = new(1, 1);
    private readonly string _path;
    private FileStream? _held;

    internal ProcessGate(string name, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".CleanMachine-locks");
        _path = Path.Combine(directory, name + ".lock");
    }

    internal async Task WaitAsync(CancellationToken token = default)
    {
        await _local.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (NativeSafety.IsReparsePoint(Path.GetDirectoryName(_path)!))
                throw new IOException("The coordination directory is redirected.");
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    _held = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
                {
                    if (deadline.Elapsed > TimeSpan.FromMinutes(2))
                        throw new IOException("Another CleanMachine process is still using this resource.", ex);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }
        catch { _local.Release(); throw; }
    }

    internal void Wait(CancellationToken token = default) => WaitAsync(token).GetAwaiter().GetResult();

    internal void Release()
    {
        _held?.Dispose();
        _held = null;
        _local.Release();
    }
}
