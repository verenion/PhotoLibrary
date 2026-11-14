using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.Core.Backup;

public sealed record BackupProgress(int Done, int Total, string? Current);

/// <summary>
/// What a backup run did. <paramref name="Archived"/>: files no longer in the library (moved to _Archive in a plain
/// backup; kept in older versions in an encrypted one). <paramref name="Expired"/>: old encrypted versions dropped.
/// </summary>
public sealed record BackupResult(int Copied, int Updated, int Unchanged, int Archived, long BytesCopied,
    IReadOnlyList<string> Errors, TimeSpan Elapsed, int Expired = 0)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// One-way backup of the library folder into a backup folder, laid out exactly like the library.
/// <para>
/// Nothing in the backup is ever deleted or overwritten in place. A file that changed in the library, or was
/// moved or deleted there, has its old backup copy moved to <c>_Archive\&lt;run time&gt;\</c> first, so a mistake
/// or corruption in the library can't destroy the backup. Vaults are copied as they are: still encrypted.
/// </para>
/// </summary>
public static class BackupService
{
    /// <summary>Holds a snapshot of Keepsake's own database (albums, AI index, settings).</summary>
    public const string AppDataFolder = "_Keepsake";
    /// <summary>Old versions and files no longer in the library, one sub-folder per backup run.</summary>
    public const string ArchiveFolder = "_Archive";

    private const string TempSuffix = ".kstmp";
    // FAT/exFAT drives store modification times to 2 seconds.
    private static readonly long MTimeTolerance = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>Why <paramref name="destination"/> can't be used, or null when it's fine.</summary>
    public static string? ValidatePlain(string library, string destination) =>
        Validate(library, destination) ?? (EncryptedBackup.IsEncryptedBackup(destination)
            ? "This folder holds an encrypted Keepsake backup. Choose another folder for a plain copy, or switch the format to Encrypted."
            : null);

    /// <summary>Checks common to both formats: the folder exists and isn't inside (or around) the library.</summary>
    public static string? Validate(string library, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return "No backup folder chosen.";
        var lib = Normalise(library);
        var dest = Normalise(destination);
        if (!Directory.Exists(lib)) return $"The library folder isn't available: {library}";
        if (!Directory.Exists(dest)) return $"The backup folder isn't available (is the drive connected?): {destination}";
        if (Scanner.PathComparer.Equals(lib, dest)) return "The backup folder can't be the library itself.";
        if (IsUnder(dest, lib)) return "The backup folder can't be inside the library.";
        if (IsUnder(lib, dest)) return "The backup folder can't contain the library.";
        return null;
    }

    /// <param name="snapshotDb">Writes a consistent copy of the app database to the given path; null to skip.</param>
    public static BackupResult Run(string library, string destination, Action<string>? snapshotDb,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();
        if (ValidatePlain(library, destination) is { } problem) return new(0, 0, 0, 0, 0, [problem], watch.Elapsed);

        var lib = Normalise(library);
        var dest = Normalise(destination);
        var archive = Path.Combine(dest, ArchiveFolder, started.ToString("yyyy-MM-dd HHmmss"));
        int copied = 0, updated = 0, unchanged = 0, archived = 0;
        long bytes = 0;

        // 1. Copy new and changed files.
        var sources = ListFiles(lib, skipTopLevel: null, errors, out var listingComplete);
        var seen = new HashSet<string>(Scanner.PathComparer);
        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(lib, sources[i]);
            seen.Add(rel);
            if (i % 25 == 0) progress?.Report(new(i, sources.Count, rel));
            try
            {
                var src = new FileInfo(sources[i]);
                var target = new FileInfo(Path.Combine(dest, rel));
                if (target.Exists)
                {
                    if (target.Length == src.Length && Math.Abs(target.LastWriteTimeUtc.Ticks - src.LastWriteTimeUtc.Ticks) <= MTimeTolerance)
                    {
                        unchanged++;
                        continue;
                    }
                    CopyIn(src, target.FullName + TempSuffix, ct);
                    MoveToArchive(target.FullName, Path.Combine(archive, rel));
                    File.Move(target.FullName + TempSuffix, target.FullName);
                    updated++;
                }
                else
                {
                    CopyIn(src, target.FullName + TempSuffix, ct);
                    File.Move(target.FullName + TempSuffix, target.FullName);
                    copied++;
                }
                bytes += src.Length;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{rel}: {ex.Message}"); }
        }

        // 2. Archive backup copies of files that are no longer in the library. Skipped if any library folder
        //    couldn't be read, so an unreadable folder never looks like a deleted one.
        if (listingComplete && sources.Count > 0)
        {
            var stale = ListFiles(dest, skipTopLevel: [AppDataFolder, ArchiveFolder], errors, out _);
            foreach (var path in stale)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(dest, path);
                if (seen.Contains(rel)) continue;
                try
                {
                    if (path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) { File.Delete(path); continue; }
                    MoveToArchive(path, Path.Combine(archive, rel));
                    archived++;
                }
                catch (Exception ex) { errors.Add($"{rel}: {ex.Message}"); }
            }
            RemoveEmptyFolders(dest);
        }
        else if (!listingComplete)
        {
            errors.Add("Some library folders couldn't be read, so nothing was archived this time.");
        }

        // 3. Snapshot the database last, so it describes the files that are now in the backup.
        if (snapshotDb is not null)
        {
            try
            {
                var dir = Directory.CreateDirectory(Path.Combine(dest, AppDataFolder)).FullName;
                var tmp = Path.Combine(dir, "library.sqlite" + TempSuffix);
                File.Delete(tmp);
                snapshotDb(tmp);
                File.Move(tmp, Path.Combine(dir, "library.sqlite"), overwrite: true);
            }
            catch (Exception ex) { errors.Add($"Database snapshot: {ex.Message}"); }
        }

        progress?.Report(new(sources.Count, sources.Count, null));
        return new(copied, updated, unchanged, archived, bytes, errors, watch.Elapsed);
    }

    private static void CopyIn(FileInfo src, string tmp, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        using (var input = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            input.CopyToAsync(output, 1 << 20, ct).GetAwaiter().GetResult();
        }
        File.SetLastWriteTimeUtc(tmp, src.LastWriteTimeUtc);
    }

    private static void MoveToArchive(string path, string archived)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Move(path, archived, overwrite: true);
    }

    /// <summary>All files under <paramref name="root"/>, not following links. <paramref name="complete"/> is false if any folder couldn't be read.</summary>
    internal static List<string> ListFiles(string root, string[]? skipTopLevel, List<string> errors, out bool complete)
    {
        complete = true;
        var files = new List<string>();
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is DirectoryInfo sub)
                    {
                        if (dir == root && skipTopLevel?.Contains(sub.Name, StringComparer.OrdinalIgnoreCase) == true) continue;
                        stack.Push(sub.FullName);
                    }
                    else if (!entry.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                errors.Add($"{dir}: {ex.Message}");
            }
        }
        return files;
    }

    private static void RemoveEmptyFolders(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals(AppDataFolder, StringComparison.OrdinalIgnoreCase) || name.Equals(ArchiveFolder, StringComparison.OrdinalIgnoreCase)) continue;
            RemoveIfEmpty(dir);
        }

        static void RemoveIfEmpty(string dir)
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir)) RemoveIfEmpty(sub);
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
