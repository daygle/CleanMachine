using System.Security.Cryptography;
using System.Text.Json;

namespace CleanMachine.Windows;

/// <summary>What CleanMachine recorded about one restore point at the moment it
/// exported it: which file it wrote, which registry root that file holds, when,
/// how large it was, and a hash of the exact bytes.</summary>
internal sealed class BackupProvenanceEntry
{
    public string FileName { get; set; } = string.Empty;
    public string KeyRoot { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>The single authoritative record of which restore points this app
/// actually created.
/// <para>
/// This exists because "a <c>.reg</c> file sitting in the backups folder" is not
/// evidence of anything. The backup directories include <c>%TEMP%</c>, which any
/// process running as the user can write, and a <c>.reg</c> file is a script:
/// <c>reg import</c> will apply whatever keys and values it contains. Without a
/// record of provenance, a planted file is indistinguishable from a real restore
/// point, and its name and timestamp - the only things the Backups page used to
/// show - are both under the writer's control.
/// </para>
/// <para>
/// So the app records what it exported and re-checks it before restoring. The
/// manifest deliberately lives in ONE place (<see cref="ManifestPath"/>, the
/// durable data root) and deliberately does <i>not</i> live in a backup
/// directory: dropping a file next to the backups must not be able to drop a
/// matching record next to it.
/// </para>
/// <para>
/// This is a trust boundary against files the user (or something running as
/// them) did not put there, and against a convincing-looking fake in the
/// Backups list. It is not a boundary against code already running as this user:
/// such code can write the registry directly, and no file-based scheme changes
/// that. See SECURITY.md.</para>
/// </summary>
internal static class BackupProvenance
{
    /// <summary>Oldest records are dropped past this. A restore point nobody has
    /// touched in years is not what someone opens the Backups page to find, and
    /// the manifest must stay small enough to read on a UI thread.</summary>
    internal const int MaxEntries = 500;

    /// <summary>A manifest larger than this is treated as untrustworthy and
    /// ignored rather than parsed, so a hand-crafted file cannot turn opening the
    /// Backups page into an unbounded allocation.</summary>
    internal const long MaxManifestBytes = 4L * 1024 * 1024;

    private static readonly object Sync = new();

    /// <summary>The one place provenance is recorded. Deliberately outside every
    /// backup directory (see the type summary).</summary>
    internal static string ManifestPath =>
        Path.Combine(AppDataPaths.Root, "backup-provenance.json");

    /// <summary>Uppercase hex SHA-256 of a file's current bytes.</summary>
    internal static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Every recorded restore point, oldest-last. Never throws: a missing,
    /// unreadable, oversized, or malformed manifest reads as "nothing is provenanced",
    /// which fails safe because nothing unprovenanced can then be restored.</summary>
    internal static IReadOnlyList<BackupProvenanceEntry> Load() => Load(ManifestPath);

    /// <summary>Overload taking the manifest path, so tests can exercise this
    /// against a scratch file instead of the user's real data folder.</summary>
    internal static IReadOnlyList<BackupProvenanceEntry> Load(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return [];
            if (new FileInfo(manifestPath).Length > MaxManifestBytes) return [];
            return JsonSerializer.Deserialize<List<BackupProvenanceEntry>>(
                       File.ReadAllBytes(manifestPath))
                   ?? [];
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Records (or replaces) the entry for a file just exported from
    /// <paramref name="keyRoot"/>. Best-effort: a provenance write that fails must
    /// not fail the export, it just means that restore point is not restorable.</summary>
    internal static void Record(string filePath, string keyRoot, DateTimeOffset createdAtUtc)
        => Record(ManifestPath, filePath, keyRoot, createdAtUtc);

    /// <summary>Overload taking the manifest path (test seam - see
    /// <see cref="Load(string)"/>).</summary>
    internal static void Record(string manifestPath, string filePath, string keyRoot, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(keyRoot) || !File.Exists(filePath)) return;

        string hash;
        long size;
        try
        {
            hash = ComputeSha256(filePath);
            size = new FileInfo(filePath).Length;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var name = Path.GetFileName(filePath);
        lock (Sync)
        {
            try
            {
                var entries = Load(manifestPath).ToList();
                // Keyed by file name: a restore point may be read back from any
                // backup directory (reg.exe and the app do not always agree on
                // %LOCALAPPDATA%), so the directory is not part of its identity.
                entries.RemoveAll(e => string.Equals(e.FileName, name, StringComparison.OrdinalIgnoreCase));
                entries.Add(new BackupProvenanceEntry
                {
                    FileName = name,
                    KeyRoot = keyRoot,
                    CreatedAtUtc = createdAtUtc,
                    SizeBytes = size,
                    Sha256 = hash
                });
                if (entries.Count > MaxEntries)
                    entries = entries.OrderByDescending(e => e.CreatedAtUtc).Take(MaxEntries).ToList();
                Write(manifestPath, entries);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The recorded entry for a file whose bytes still hash to what was
    /// recorded, or null when there is no entry, the size differs, or the content
    /// was changed after export. Matching on the hash is what makes replacing a
    /// real backup's bytes with a different payload detectable.</summary>
    internal static BackupProvenanceEntry? Match(string filePath, IReadOnlyList<BackupProvenanceEntry> entries)
    {
        var name = Path.GetFileName(filePath);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(entry.Sha256) || string.IsNullOrWhiteSpace(entry.KeyRoot)) continue;
            try
            {
                if (!File.Exists(filePath)) return null;
                // Cheap rejection first: re-hashing every candidate on every
                // Backups-page load would be wasteful, and a size change is
                // conclusive anyway.
                if (entry.SizeBytes > 0 && new FileInfo(filePath).Length != entry.SizeBytes) continue;
                if (!string.Equals(ComputeSha256(filePath), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;
                return entry;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    private static void Write(string manifestPath, List<BackupProvenanceEntry> entries)
    {
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
                entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally
        {
            // Same discipline as AppSettings.SaveAsync: never leave a stray temp
            // file behind for a later run to trip over.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
