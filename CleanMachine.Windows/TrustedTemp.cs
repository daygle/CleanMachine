using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

internal static class TrustedTemp
{
    // LocalAppData's known-folder identity, not GetTempPath/TEMP/TMP or USERPROFILE.
    private static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    internal static string Root { get; } = Resolve();
    private static string Resolve()
    {
        if (!OperatingSystem.IsWindows()) return Path.GetTempPath(); // portable tests only
        var id = LocalAppData;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try
        {
            var local = Marshal.PtrToStringUni(pointer);
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                throw new IOException("Cannot resolve the per-user temporary cleanup root.");
            return Path.Combine(local, "Temp");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
