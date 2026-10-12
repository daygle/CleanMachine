using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CleanMachine.Windows;

/// <summary>Windows cleanup deletes the same entry it validates, by handle.
/// Each name is opened relative to a held, non-reparse parent. Parent handles
/// disallow writes/deletes so neither rename nor reparse-data replacement can
/// redirect the traversal. Share conflicts fail closed; no path-delete fallback
/// is used on Windows. Multiple-hardlink files are deliberately refused.</summary>
internal static class SafeDeletion
{
    internal static long DeleteFile(string path, string allowedRoot, DateTime? olderThan = null)
    {
        if (!NativeSafety.IsWithin(path, allowedRoot)) throw new IOException("Deletion is outside the allowed root.");
        if (!OperatingSystem.IsWindows())
        {
            // Portable scratch-file tests only; the application is Windows-only.
            if (NativeSafety.IsReparsePoint(path)) throw new IOException("Redirected cleanup path.");
            var info = new FileInfo(path);
            if (olderThan is { } cutoff && info.LastWriteTimeUtc > cutoff) throw new IOException("Recently modified.");
            var length = info.Length;
            File.Delete(path);
            return length;
        }
        using var lease = Pin(path, delete: true);
        var data = lease.Information;
        if ((data.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            throw new IOException("Not a writable ordinary file.");
        if (data.NumberOfLinks != 1) throw new IOException("Hard-linked file is not a cleanup candidate.");
        var lastWrite = DateTime.FromFileTimeUtc(((long)data.LastWrite.High << 32) | data.LastWrite.Low);
        if (olderThan is { } threshold && lastWrite > threshold) throw new IOException("Recently modified.");
        var size = ((long)data.SizeHigh << 32) | data.SizeLow;
        MarkDeleted(lease.Handle);
        return size;
    }

    internal static void DeleteEmptyDirectory(string path, string allowedRoot)
    {
        if (!NativeSafety.IsWithin(path, allowedRoot)) throw new IOException("Deletion is outside the allowed root.");
        if (!OperatingSystem.IsWindows())
        {
            if (NativeSafety.IsReparsePoint(path)) throw new IOException("Redirected cleanup directory.");
            Directory.Delete(path, recursive: false);
            return;
        }
        using var lease = Pin(path, delete: true);
        if ((lease.Information.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("Expected a directory.");
        // The filesystem refuses disposition on a nonempty directory, atomically.
        MarkDeleted(lease.Handle);
    }

    internal static long DeleteTree(string path, CancellationToken token = default)
    {
        long bytes = 0;
        foreach (var file in FileEnumeration.Files(path, token))
        {
            var size = DeleteFile(file, path);
            bytes += size;
            CleanupJournal.Record(1, size);
        }
        foreach (var directory in FileEnumeration.Directories(path, token).OrderByDescending(p => p.Length))
        {
            token.ThrowIfCancellationRequested();
            DeleteEmptyDirectory(directory, path);
        }
        token.ThrowIfCancellationRequested();
        DeleteEmptyDirectory(path, path);
        return bytes;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal SafeFileHandle Handle => _handles[^1];
        internal FileInformation Information { get; private set; }
        internal void Add(SafeFileHandle handle)
        {
            _handles.Add(handle);
            if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("A path component is a reparse point.");
            Information = info;
        }
        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        }
    }

    internal static Lease Pin(string path, bool delete = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native path pinning requires Windows.");
        var full = Path.GetFullPath(path);
        if (!IsSupportedPath(full)) throw new IOException("Only ordinary absolute local paths can be pinned.");
        var root = Path.GetPathRoot(full)!;
        var names = full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var lease = new Lease();
        try
        {
            var rootHandle = CreateFileW(root, 0x00100080, 1 /* share read only */, IntPtr.Zero,
                3, 0x02200000 /* backup semantics + open reparse point */, IntPtr.Zero);
            if (rootHandle.IsInvalid) { rootHandle.Dispose(); ThrowLastError(); }
            lease.Add(rootHandle);
            for (var i = 0; i < names.Length; i++)
            {
                var leaf = i == names.Length - 1;
                var name = names[i];
                if (name.Contains(':') || name is "." or "..") throw new IOException("Unsupported path component.");
                var text = Marshal.StringToHGlobalUni(name);
                var unicodePointer = IntPtr.Zero;
                try
                {
                    var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text };
                    unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
                    Marshal.StructureToPtr(unicode, unicodePointer, false);
                    var attributes = new ObjectAttributes
                    {
                        Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = lease.Handle.DangerousGetHandle(),
                        ObjectName = unicodePointer, Attributes = 0x40 /* case-insensitive */
                    };
                    var status = NtCreateFile(out var child,
                        0x00100080u | (leaf && delete ? 0x00010000u : 0), ref attributes, out _, IntPtr.Zero,
                        0, 1 /* share read only */, 1 /* FILE_OPEN */,
                        0x00200020 /* OPEN_REPARSE_POINT + SYNCHRONOUS_IO_NONALERT */, IntPtr.Zero, 0);
                    if (status < 0)
                    {
                        child?.Dispose();
                        throw new IOException("Cannot safely open cleanup entry.", new Win32Exception((int)RtlNtStatusToDosError(status)));
                    }
                    lease.Add(child);
                    if (!leaf && (lease.Information.Attributes & (uint)FileAttributes.Directory) == 0)
                        throw new IOException("Ancestor is not a directory.");
                }
                finally
                {
                    if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
                    Marshal.FreeHGlobal(text);
                }
            }
            // A path returned by a filesystem filter must still name the expected
            // object. Never bypass MSIX redirection by assuming a different view.
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(lease.Handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve held cleanup entry.");
            var resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            if (!Path.TrimEndingDirectorySeparator(resolved).Equals(Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Opened cleanup entry was redirected.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static bool IsSupportedPath(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
           && !path[2..].Contains(':') && !path.Contains('/') && !path.Contains('\0');

    private static void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new Disposition { Delete = true };
        if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref disposition, 1)) ThrowLastError();
    }
    private static void ThrowLastError() => throw new IOException("Native cleanup authorization failed.", new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)] internal struct FileTime { internal uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
    {
        internal uint Attributes;
        internal FileTime Creation, LastAccess, LastWrite;
        internal uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.U1)] internal bool Delete; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref Disposition information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}
