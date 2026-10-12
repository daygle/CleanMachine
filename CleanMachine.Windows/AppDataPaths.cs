namespace CleanMachine.Windows;

/// <summary>Single source of truth for CleanMachine's per-user data folder
/// (settings, statistics, activity history, registry backups).
///
/// The folder has to survive an uninstall, which rules out %LOCALAPPDATA% on a
/// packaged (MSIX) install for two independent reasons, both verified on a real
/// Store install:
/// <list type="bullet">
/// <item>This process's own writes under %LOCALAPPDATA% are redirected into
/// ...\Packages\&lt;family&gt;\LocalCache\Local\, and Windows deletes that folder
/// when the package is removed - silently, and without asking.</item>
/// <item>Tools this app spawns are NOT redirected, so reg.exe writes to the
/// literal %LOCALAPPDATA% path, which therefore never physically exists and
/// makes every registry backup fail.</item>
/// </list>
/// <para>Stripping the "\Packages\" segment from the known folder (see
/// <see cref="RealLocalAppData"/>) defeats neither of those: the redirector keys
/// off the path, not off the API the path came from, so a hand-built
/// %LOCALAPPDATA% path is still redirected. The durable folder is therefore
/// <see cref="ProfileRoot"/>, under the user profile and outside every known
/// folder that gets redirected, and existing data is copied there on first
/// launch.</para></summary>
internal static class AppDataPaths
{
    internal const string FolderName = "CleanMachine";

    private const string MigrationMarkerName = ".legacy-migration-v1-complete";
    private const string MigrationMarkerContents = "CleanMachine legacy migration v1 complete\n";

    internal sealed record MigrationResult(string Root, bool Complete, string? Error);

    /// <summary>The canonical data root, resolved once per process.</summary>
    internal static string Root { get; } = SafeResolve();

    /// <summary>The durable data folder: the user profile, which is outside every
    /// known folder MSIX redirects, so this process and the tools it spawns both
    /// resolve it to the same real path, and it outlives the package.</summary>
    internal static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderName);

    /// <summary>The non-package %LOCALAPPDATA%\CleanMachine folder: a legacy
    /// location and a migration source. Still the data root for unpackaged builds,
    /// where nothing is redirected.</summary>
    internal static string RealRoot => Path.Combine(
        RealLocalAppData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        FolderName);

    /// <summary>The folder GetFolderPath reports under MSIX (inside the package).
    /// Identical to <see cref="RealRoot"/> in unpackaged builds. A migration source
    /// and the folder Windows deletes at uninstall.</summary>
    internal static string PackageLocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    /// <summary>Strips the MSIX known-folder redirection: under a packaged app
    /// %LOCALAPPDATA% is ...\Packages\&lt;family&gt;\..., so everything before
    /// that segment is the real %LOCALAPPDATA%. Unpackaged paths pass through
    /// unchanged (they contain no \Packages\ segment).
    ///
    /// Kept for the legacy/migration paths and for its own unit test. It is NOT
    /// on its own a way to escape the redirector - see the type summary.</summary>
    internal static string RealLocalAppData(string localAppData)
    {
        var index = localAppData.IndexOf("\\Packages\\", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? localAppData[..index] : localAppData;
    }

    /// <summary>Copies any data from legacy roots to the durable folder.
    /// Existing destination files are preserved. Completion is explicit: partial
    /// copies never get a success marker, so the next launch retries them.</summary>
    internal static MigrationResult TryMigrateToDurableRoot(string durable, params string[] legacyRoots)
    {
        // Migration is a one-time launch path. Bound not only tree breadth/depth
        // and elapsed time but also total bytes read/written and hashed, so a very
        // large profile item cannot stall startup or consume unbounded disk space.
        var budget = new WorkBudget(maxEntries: 100000, maxDepth: 4, maxBytes: 512L * 1024 * 1024);
        try
        {
            Directory.CreateDirectory(durable);
            if (NativeSafety.IsReparsePoint(durable))
                return new(durable, false, "The destination data folder is a link or redirected path.");

            // A valid marker means the initial migration already finished. Do not
            // reconcile old legacy trees on subsequent launches/uninstalls: users
            // may have deleted stale package-local copies since then, and copying
            // them again would resurrect removed settings/history/backups.
            if (HasValidMigrationMarker(durable)) return new(durable, true, null);

            // An older success marker may exist from an earlier launch. Remove it
            // before every reconciliation pass so a later failure cannot leave a
            // stale "complete" claim behind. It is recreated only after all copies
            // finish successfully.
            InvalidateMigrationMarker(durable);
            var complete = true;
            foreach (var legacy in legacyRoots)
            {
                if (string.IsNullOrWhiteSpace(legacy)) continue;
                var fullLegacy = Path.GetFullPath(legacy);
                var fullDurable = Path.GetFullPath(durable);
                if (string.Equals(fullLegacy, fullDurable, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CopyTree(fullLegacy, fullDurable, budget)) complete = false;
            }

            if (!complete)
                return new(FindReachableLegacyRoot(legacyRoots), false,
                    "Some legacy files could not be copied. Their original files are still in place; close applications using them and retry.");

            WriteMigrationMarker(durable);
            return new(durable, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   System.Security.SecurityException or NotSupportedException)
        {
            return new(FindReachableLegacyRoot(legacyRoots), false,
                $"The durable data folder could not be verified: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // These helpers are used during type initialization as well as uninstall.
            // Convert unexpected filesystem/API failures to an explicit failure so
            // callers do not mistake a partial migration for a thrown UI crash.
            return new(FindReachableLegacyRoot(legacyRoots), false,
                $"The durable data folder could not be verified: {ex.Message}");
        }
    }

    /// <summary>Compatibility wrapper used by callers which can safely fall back
    /// to a reachable legacy root; callers that promise data preservation must
    /// inspect <see cref="TryMigrateToDurableRoot"/> and reject incomplete copies.</summary>
    internal static string MigrateToDurableRoot(string durable, params string[] legacyRoots)
        => TryMigrateToDurableRoot(durable, legacyRoots).Root;

    internal static MigrationResult CopyToDurableRootResult()
        => TryMigrateToDurableRoot(ProfileRoot, PackageLocalRoot, RealRoot);

    /// <summary>Pushes package-local data out to the durable folder. Called before
    /// the package is removed so that "keep my data" is actually true.</summary>
    internal static string CopyToDurableRoot()
        => CopyToDurableRootResult().Root;

    private static bool CopyTree(string source, string target, WorkBudget budget, int depth = 0)
    {
        try
        {
            budget.Check(depth);
            var sourceAttributes = ExistingAttributes(source);
            if (sourceAttributes is null) return true; // a genuinely absent legacy root is fine
            if ((sourceAttributes.Value & FileAttributes.ReparsePoint) != 0) return true; // linked sources are intentionally excluded
            if ((sourceAttributes.Value & FileAttributes.Directory) == 0 || depth > 4) return false;
            Directory.CreateDirectory(target);
            if (NativeSafety.IsReparsePoint(target)) return false;

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                budget.Check(depth);
                if (string.Equals(Path.GetFileName(file), MigrationMarkerName, StringComparison.OrdinalIgnoreCase)
                    || NativeSafety.IsReparsePoint(file)) continue;
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (!CopyFileSafely(file, destination, budget)) return false;
            }

            var complete = true;
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                budget.Check(depth + 1);
                if (NativeSafety.IsReparsePoint(directory)) continue;
                if (!CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)), budget, depth + 1))
                    complete = false;
            }
            return complete;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static bool CopyFileSafely(string source, string destination, WorkBudget budget)
    {
        var existing = ExistingAttributes(destination);
        if (existing is { } attributes)
        {
            // Never trust/overwrite a redirected destination. Existing ordinary
            // durable data wins over legacy data by design.
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            ".cm-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (NativeSafety.IsReparsePoint(source)) return false;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    budget.Check();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    budget.ConsumeBytes(read);
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
            }
            budget.Check();

            // A successful File.Copy alone is not proof that the staged bytes are
            // complete. Verify content before the atomic publish; interrupted or
            // short copies remain a retryable temp file instead of masquerading as
            // the destination.
            using var original = File.OpenRead(source);
            using var staged = File.OpenRead(temporary);
            if (original.Length != staged.Length
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    HashBounded(original, budget), HashBounded(staged, budget)))
                return false;

            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (ExistingAttributes(destination) is { } raced
                                      && (raced & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            {
                // Another process created a regular durable file first. Preserve it.
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static byte[] HashBounded(Stream stream, WorkBudget budget)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            budget.Check();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string FindReachableLegacyRoot(IEnumerable<string> roots)
        => roots.FirstOrDefault(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) ?? PackageLocalRoot;

    private static void InvalidateMigrationMarker(string durable)
    {
        var marker = Path.Combine(durable, MigrationMarkerName);
        if (File.Exists(marker) && NativeSafety.IsReparsePoint(marker))
            throw new IOException("The migration marker is redirected.");
        if (File.Exists(marker)) File.Delete(marker);
    }

    private static void WriteMigrationMarker(string durable)
    {
        var marker = Path.Combine(durable, MigrationMarkerName);
        if (File.Exists(marker) && NativeSafety.IsReparsePoint(marker))
            throw new IOException("The migration marker is redirected.");
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(MigrationMarkerContents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, marker, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool HasValidMigrationMarker(string durable)
    {
        try
        {
            var marker = Path.Combine(durable, MigrationMarkerName);
            return File.Exists(marker) && !NativeSafety.IsReparsePoint(marker)
                && new FileInfo(marker).Length == System.Text.Encoding.UTF8.GetByteCount(MigrationMarkerContents)
                && File.ReadAllText(marker) == MigrationMarkerContents;
        }
        catch { return false; }
    }

    private static string SafeResolve()
    {
        try
        {
            // Unpackaged: nothing is redirected, so the ordinary per-user
            // application-data location is both correct and durable.
            if (!ScheduleService.IsMsix) return RealRoot;
            // Only an atomically-written marker proves every eligible legacy file
            // was copied. A settings file alone may have arrived before backups or
            // other records and is not evidence of a complete migration.
            if (HasValidMigrationMarker(ProfileRoot)) return ProfileRoot;
            return TryMigrateToDurableRoot(ProfileRoot, PackageLocalRoot, RealRoot).Root;
        }
        catch
        {
            // Never let a migration hiccup surface as a TypeInitializationException
            // that would take every store down with it.
            return PackageLocalRoot;
        }
    }
}
