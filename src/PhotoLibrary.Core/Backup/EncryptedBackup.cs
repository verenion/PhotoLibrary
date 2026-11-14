using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.Core.Backup;

/// <summary>The backup folder's key file: the backup key, wrapped once under the password and once under the recovery key.</summary>
public sealed record BackupKeyFile(string Format, int Version, string BackupId, string Kdf, int MemoryKiB, int Iterations, int Parallelism,
    string Salt, string WrappedKey, string RecoverySalt, string RecoveryWrappedKey, DateTime CreatedUtc);

/// <summary>One backup run: when it was taken and what it holds.</summary>
public sealed record SnapshotInfo(string Id, DateTime TakenUtc, int Files, long Bytes);

/// <summary>One file in a snapshot: where it lived in the library, and the encrypted object holding its bytes.</summary>
public sealed record SnapshotEntry(string Path, long Size, long MTime, string Object, string Sha256);

public sealed record RestoreResult(int Restored, int Skipped, long Bytes, IReadOnlyList<string> Errors, TimeSpan Elapsed)
{
    public bool Ok => Errors.Count == 0;
}

public sealed record VerifyResult(int Checked, IReadOnlyList<string> Problems, TimeSpan Elapsed)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Encrypted, versioned backups. The backup folder holds:
/// <list type="bullet">
/// <item><c>keepsake-backup.json</c>: the random backup key, wrapped under the password (Argon2id) and again under
/// a recovery key, exactly like a vault's key file;</item>
/// <item><c>objects\ab\&lt;id&gt;.kso</c>: one encrypted file per distinct file content. The id is an HMAC of the
/// content's SHA-256 under a key derived from the backup key, so identical files are stored once and nobody
/// can tell from the names which files are in the backup. Contents that shrink with Brotli are compressed
/// first; photos and videos almost never do, so they're stored as they are;</item>
/// <item><c>lists\&lt;time&gt;.ksl</c>: one encrypted list per run, mapping every library path to its object.
/// Each list is a version of the library that can be restored.</item>
/// </list>
/// Files use the vault format (AES-256-GCM in 1 MiB chunks under a per-file HKDF key), so names, folders, dates
/// and contents are all unreadable without the password or recovery key. Only the number and size of objects show.
/// </summary>
public static class EncryptedBackup
{
    public const string KeyFileName = "keepsake-backup.json";
    public const string ObjectsDir = "objects", ListsDir = "lists";
    private const string ObjectExt = ".kso", ListExt = ".ksl", TempSuffix = ".kstmp";
    private static readonly string DatabasePath = Path.Combine(BackupService.AppDataFolder, "library.sqlite");
    private static readonly long MTimeTolerance = TimeSpan.FromSeconds(2).Ticks;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Formats that are already compressed: trying Brotli on them only costs time.</summary>
    private static readonly HashSet<string> Precompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".webp", ".avif", ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".avi",
        ".wmv", ".mts", ".m2ts", ".mp", ".cover", ".cr3", ".zip", ".7z", ".rar", ".gz", ".mp3", ".m4a", ".aac", ".plv",
    };

    public static bool IsEncryptedBackup(string dir) => File.Exists(Path.Combine(dir, KeyFileName));

    /// <summary>Why <paramref name="destination"/> can't hold an encrypted backup, or null.</summary>
    public static string? Validate(string library, string destination)
    {
        if (BackupService.Validate(library, destination) is { } problem) return problem;
        if (IsEncryptedBackup(destination)) return null;
        if (Directory.EnumerateFileSystemEntries(destination).Any(e => !MediaKinds.IgnoredNames.Contains(Path.GetFileName(e))))
            return "An encrypted backup needs an empty folder (or an existing Keepsake encrypted backup). This folder already has other things in it.";
        return null;
    }

    // ---- keys

    /// <summary>Sets up a new encrypted backup in an empty folder. Returns the backup key and the recovery key to show the user once.</summary>
    public static (byte[] Key, string RecoveryKey) Create(string destination, string password, KdfParams kdf)
    {
        Directory.CreateDirectory(destination);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var key = VaultCrypto.NewKey();
        var recovery = NewRecoveryKey();
        var (salt, wrapped) = Wrap(password, key, id, kdf);
        var (rSalt, rWrapped) = Wrap(NormaliseRecoveryKey(recovery), key, id, kdf);
        WriteKeyFile(destination, new BackupKeyFile("keepsake-backup", 1, id, "argon2id", kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism,
            salt, wrapped, rSalt, rWrapped, DateTime.UtcNow));
        Directory.CreateDirectory(Path.Combine(destination, ObjectsDir));
        Directory.CreateDirectory(Path.Combine(destination, ListsDir));
        return (key, recovery);
    }

    public static BackupKeyFile ReadKeyFile(string destination) =>
        JsonSerializer.Deserialize<BackupKeyFile>(File.ReadAllText(Path.Combine(destination, KeyFileName)))
        ?? throw new VaultAuthenticationException("The backup's key file is damaged.");

    /// <summary>The backup key from the password or the recovery key; null when it's neither.</summary>
    public static byte[]? TryUnlock(string destination, string secret)
    {
        var kf = ReadKeyFile(destination);
        if (kf.Kdf != "argon2id") throw new VaultAuthenticationException($"Unknown key derivation “{kf.Kdf}”.");
        var p = new KdfParams(kf.MemoryKiB, kf.Iterations, kf.Parallelism);
        if (Unwrap(secret, kf.Salt, kf.WrappedKey, kf.BackupId, p) is { } key) return key;
        var asRecovery = NormaliseRecoveryKey(secret);
        return asRecovery.Length == RecoveryChars ? Unwrap(asRecovery, kf.RecoverySalt, kf.RecoveryWrappedKey, kf.BackupId, p) : null;
    }

    /// <summary>
    /// Sets a new password, given the current password or the recovery key. The recovery key stays the same, and
    /// so does the key-derivation strength, which both share.
    /// </summary>
    public static bool TryChangePassword(string destination, string currentSecret, string newPassword)
    {
        if (TryUnlock(destination, currentSecret) is not { } key) return false;
        try
        {
            var kf = ReadKeyFile(destination);
            var (salt, wrapped) = Wrap(newPassword, key, kf.BackupId, new KdfParams(kf.MemoryKiB, kf.Iterations, kf.Parallelism));
            WriteKeyFile(destination, kf with { Salt = salt, WrappedKey = wrapped });
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static (string Salt, string Wrapped) Wrap(string secret, byte[] key, string backupId, KdfParams kdf)
    {
        var salt = VaultCrypto.NewSalt();
        var kek = VaultCrypto.DeriveKek(secret, salt, kdf);
        try { return (Convert.ToBase64String(salt), Convert.ToBase64String(VaultCrypto.WrapKey(kek, key, "backup:" + backupId))); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static byte[]? Unwrap(string secret, string salt, string wrapped, string backupId, KdfParams p)
    {
        var kek = VaultCrypto.DeriveKek(secret, Convert.FromBase64String(salt), p);
        try { return VaultCrypto.TryUnwrapKey(kek, Convert.FromBase64String(wrapped), "backup:" + backupId); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static void WriteKeyFile(string destination, BackupKeyFile kf)
    {
        var path = Path.Combine(destination, KeyFileName);
        File.WriteAllText(path + TempSuffix, JsonSerializer.Serialize(kf, Json));
        File.Move(path + TempSuffix, path, overwrite: true);
    }

    // Recovery keys: 160 random bits as 32 characters with no look-alikes (no I, O, 0 or 1), in groups of four.
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int RecoveryChars = 32;

    private static string NewRecoveryKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(RecoveryAlphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        var raw = sb.ToString();
        return string.Join("-", Enumerable.Range(0, raw.Length / 4).Select(i => raw.Substring(i * 4, 4)));
    }

    private static string NormaliseRecoveryKey(string s) =>
        new(s.ToUpperInvariant().Where(c => RecoveryAlphabet.Contains(c)).ToArray());

    // ---- objects

    private static string ObjectId(byte[] key, string sha256Hex)
    {
        var idKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: "keepsake-backup-object-id"u8.ToArray());
        try { return Convert.ToHexString(HMACSHA256.HashData(idKey, Convert.FromHexString(sha256Hex))).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(idKey); }
    }

    private static string ObjectPath(string destination, string id) => Path.Combine(destination, ObjectsDir, id[..2], id + ObjectExt);

    private sealed record ObjectMeta(int V, string C, long N);

    private static string HashFile(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Encrypts a file into its object (compressed first when that helps). Checks the file didn't change meanwhile.</summary>
    private static void StoreObject(string destination, byte[] key, string source, string sha, string id, CancellationToken ct)
    {
        var target = ObjectPath(destination, id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var length = new FileInfo(source).Length;
        string? compressed = null;
        try
        {
            if (!Precompressed.Contains(Path.GetExtension(source)) && length > 512)
            {
                compressed = Path.Combine(Path.GetTempPath(), "keepsake-" + Guid.NewGuid().ToString("N") + TempSuffix);
                using (var input = File.OpenRead(source))
                using (var output = File.Create(compressed))
                using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
                    input.CopyToAsync(brotli, 1 << 20, ct).GetAwaiter().GetResult();
                if (new FileInfo(compressed).Length > length * 0.95) { File.Delete(compressed); compressed = null; }
            }
            var meta = JsonSerializer.SerializeToUtf8Bytes(new ObjectMeta(1, compressed is null ? "" : "br", length));
            using (var content = new FileStream(compressed ?? source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var output = new FileStream(target + TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                VaultCrypto.EncryptFile(key, meta, content, output);
            // A file edited while it was being backed up would be stored under the wrong id: check before keeping it.
            if (HashFile(source, ct) != sha)
            {
                File.Delete(target + TempSuffix);
                throw new IOException("The file changed while it was being backed up; it will be picked up next time.");
            }
            File.Move(target + TempSuffix, target, overwrite: true);
        }
        finally
        {
            if (compressed is not null) try { File.Delete(compressed); } catch (IOException) { }
        }
    }

    /// <summary>Decrypts an object into <paramref name="target"/>, undoing compression, and checks its SHA-256.</summary>
    private static void ReadObject(string destination, byte[] key, SnapshotEntry entry, string target, CancellationToken ct)
    {
        var path = ObjectPath(destination, entry.Object);
        if (!File.Exists(path)) throw new IOException("its encrypted copy is missing from the backup");
        var decrypted = target + ".dec" + TempSuffix;
        try
        {
            byte[] metaBytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var output = File.Create(decrypted))
                metaBytes = VaultCrypto.DecryptFile(key, input, output);
            var meta = JsonSerializer.Deserialize<ObjectMeta>(metaBytes) ?? throw new IOException("its encrypted copy is damaged");
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var raw = File.OpenRead(decrypted))
                using (var content = meta.C == "br" ? new BrotliStream(raw, CompressionMode.Decompress) : (Stream)raw)
                using (var output = File.Create(target))
                {
                    var buffer = new byte[1 << 20];
                    int n;
                    while ((n = content.Read(buffer)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, n);
                        output.Write(buffer, 0, n);
                    }
                }
                if (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != entry.Sha256)
                    throw new IOException("its restored bytes don't match what was backed up");
            }
        }
        finally { try { File.Delete(decrypted); } catch (IOException) { } }
    }

    // ---- snapshots (lists)

    private sealed record ListMeta(int V, DateTime CreatedUtc, int Files, long Bytes);

    public static List<SnapshotInfo> ListSnapshots(string destination, byte[] key, List<string>? damaged = null)
    {
        var dir = Path.Combine(destination, ListsDir);
        var result = new List<SnapshotInfo>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + ListExt))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var meta = JsonSerializer.Deserialize<ListMeta>(VaultCrypto.ReadMetadata(key, fs))!;
                result.Add(new SnapshotInfo(Path.GetFileNameWithoutExtension(path), meta.CreatedUtc, meta.Files, meta.Bytes));
            }
            catch (Exception ex) when (ex is IOException or VaultAuthenticationException or JsonException)
            {
                damaged?.Add(Path.GetFileName(path));
            }
        }
        return result.OrderByDescending(s => s.TakenUtc).ToList();
    }

    public static List<SnapshotEntry> ReadSnapshot(string destination, byte[] key, string id)
    {
        using var fs = File.OpenRead(Path.Combine(destination, ListsDir, id + ListExt));
        using var plain = new MemoryStream();
        VaultCrypto.DecryptFile(key, fs, plain);
        plain.Position = 0;
        using var brotli = new BrotliStream(plain, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<SnapshotEntry>>(brotli) ?? [];
    }

    private static string WriteSnapshot(string destination, byte[] key, List<SnapshotEntry> entries)
    {
        var now = DateTime.UtcNow;
        var id = now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(destination, ListsDir, id + ListExt);
        for (var n = 1; File.Exists(path); n++) path = Path.Combine(destination, ListsDir, $"{id}-{n}{ListExt}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var content = new MemoryStream();
        using (var brotli = new BrotliStream(content, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(brotli, entries);
        content.Position = 0;
        var meta = JsonSerializer.SerializeToUtf8Bytes(new ListMeta(1, now, entries.Count, entries.Sum(e => e.Size)));
        using (var output = File.Create(path + TempSuffix))
            VaultCrypto.EncryptFile(key, meta, content, output);
        File.Move(path + TempSuffix, path);
        return Path.GetFileNameWithoutExtension(path);
    }

    // ---- backing up

    /// <param name="keepFor">How long to keep older versions; null keeps them forever. The newest is always kept.</param>
    public static BackupResult Run(string library, string destination, byte[] key, Action<string>? snapshotDb, TimeSpan? keepFor,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();
        if (BackupService.Validate(library, destination) is { } problem) return new(0, 0, 0, 0, 0, [problem], watch.Elapsed);
        if (!IsEncryptedBackup(destination)) return new(0, 0, 0, 0, 0, ["This folder isn't set up for encrypted backups."], watch.Elapsed);
        var lib = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library));

        // The newest readable list says what each path held last time, so unchanged files aren't read again.
        var previous = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        if (ListSnapshots(destination, key).FirstOrDefault() is { } last)
        {
            try { foreach (var e in ReadSnapshot(destination, key, last.Id)) previous[e.Path] = e; }
            catch (Exception ex) when (ex is IOException or VaultAuthenticationException or JsonException) { }
        }

        int stored = 0, updated = 0, unchanged = 0;
        long bytes = 0;
        var entries = new List<SnapshotEntry>();
        var sources = BackupService.ListFiles(lib, skipTopLevel: null, errors, out var listingComplete);
        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(lib, sources[i]);
            if (i % 25 == 0) progress?.Report(new(i, sources.Count, rel));
            try
            {
                var fi = new FileInfo(sources[i]);
                var mtime = fi.LastWriteTimeUtc.Ticks;
                if (previous.TryGetValue(rel, out var before) && before.Size == fi.Length && Math.Abs(before.MTime - mtime) <= MTimeTolerance
                    && File.Exists(ObjectPath(destination, before.Object)))
                {
                    entries.Add(before with { Path = rel });
                    unchanged++;
                    continue;
                }
                var sha = HashFile(fi.FullName, ct);
                var id = ObjectId(key, sha);
                if (!File.Exists(ObjectPath(destination, id)))
                {
                    StoreObject(destination, key, fi.FullName, sha, id, ct);
                    bytes += fi.Length;
                    if (before is null) stored++; else updated++;
                }
                else unchanged++; // same content already backed up (a copy, or a moved file)
                entries.Add(new SnapshotEntry(rel, fi.Length, mtime, id, sha));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{rel}: {ex.Message}"); }
        }
        if (!listingComplete) errors.Add("Some library folders couldn't be read; files in them are kept from earlier versions.");

        // The database snapshot rides along as one more file.
        if (snapshotDb is not null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "keepsake-db-" + Guid.NewGuid().ToString("N") + ".sqlite");
            try
            {
                snapshotDb(tmp);
                var sha = HashFile(tmp, ct);
                var id = ObjectId(key, sha);
                if (!File.Exists(ObjectPath(destination, id))) StoreObject(destination, key, tmp, sha, id, ct);
                entries.Add(new SnapshotEntry(DatabasePath, new FileInfo(tmp).Length, DateTime.UtcNow.Ticks, id, sha));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"Database snapshot: {ex.Message}"); }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        }

        var current = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var gone = previous.Keys.Count(p => !current.Contains(p) && p != DatabasePath);
        if (sources.Count > 0 || previous.Count == 0) WriteSnapshot(destination, key, entries);
        else errors.Add("The library looked empty, so no new version was written; the earlier versions are untouched.");

        var expired = keepFor is { } keep ? Expire(destination, key, keep, errors) : 0;
        progress?.Report(new(sources.Count, sources.Count, null));
        return new(stored, updated, unchanged, gone, bytes, errors, watch.Elapsed, expired);
    }

    /// <summary>
    /// Drops versions older than <paramref name="keepFor"/> (never the newest), then deletes objects no remaining
    /// version uses. If any remaining version can't be read, no objects are deleted.
    /// </summary>
    private static int Expire(string destination, byte[] key, TimeSpan keepFor, List<string> errors)
    {
        var snapshots = ListSnapshots(destination, key);
        var cutoff = DateTime.UtcNow - keepFor;
        var old = snapshots.Skip(1).Where(s => s.TakenUtc < cutoff).ToList();
        if (old.Count == 0) return 0;
        foreach (var s in old) File.Delete(Path.Combine(destination, ListsDir, s.Id + ListExt));

        var used = new HashSet<string>(StringComparer.Ordinal);
        var damaged = new List<string>();
        var remaining = ListSnapshots(destination, key, damaged);
        if (damaged.Count > 0) { errors.Add("Some older versions couldn't be read, so no old files were cleaned up."); return old.Count; }
        foreach (var s in remaining)
        {
            try { foreach (var e in ReadSnapshot(destination, key, s.Id)) used.Add(e.Object); }
            catch (Exception ex) when (ex is IOException or VaultAuthenticationException or JsonException)
            {
                errors.Add($"Version {s.Id} couldn't be read, so no old files were cleaned up.");
                return old.Count;
            }
        }
        foreach (var path in Directory.EnumerateFiles(Path.Combine(destination, ObjectsDir), "*", SearchOption.AllDirectories).ToList())
        {
            var name = Path.GetFileName(path);
            var stale = name.EndsWith(TempSuffix, StringComparison.Ordinal) || !used.Contains(Path.GetFileNameWithoutExtension(name));
            if (!stale) continue;
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return old.Count;
    }

    // ---- restoring and checking

    /// <summary>
    /// Writes a version's files under <paramref name="target"/>, in their library folders. Existing files are never
    /// overwritten: they're skipped. Every restored file is checked against its SHA-256.
    /// </summary>
    public static RestoreResult Restore(string destination, byte[] key, string snapshotId, string target, Func<string, bool>? include,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var entries = ReadSnapshot(destination, key, snapshotId).Where(e => include?.Invoke(e.Path) ?? true).ToList();
        int restored = 0, skipped = 0;
        long bytes = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            if (i % 10 == 0) progress?.Report(new(i, entries.Count, e.Path));
            var output = Path.GetFullPath(Path.Combine(root, e.Path));
            if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{e.Path}: not a safe path; skipped.");
                continue;
            }
            if (File.Exists(output)) { skipped++; continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                ReadObject(destination, key, e, output + TempSuffix, ct);
                File.Move(output + TempSuffix, output);
                try { File.SetLastWriteTimeUtc(output, new DateTime(e.MTime, DateTimeKind.Utc)); } catch (IOException) { }
                restored++;
                bytes += e.Size;
            }
            catch (OperationCanceledException) { try { File.Delete(output + TempSuffix); } catch (IOException) { } throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException or InvalidDataException or JsonException)
            {
                try { File.Delete(output + TempSuffix); } catch (IOException) { }
                errors.Add($"{e.Path}: {ex.Message}");
            }
        }
        progress?.Report(new(entries.Count, entries.Count, null));
        return new(restored, skipped, bytes, errors, watch.Elapsed);
    }

    /// <summary>Decrypts every file in a version (the newest by default) and checks it against its SHA-256, writing nothing.</summary>
    public static VerifyResult Verify(string destination, byte[] key, string? snapshotId = null,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var problems = new List<string>();
        var damaged = new List<string>();
        var snapshots = ListSnapshots(destination, key, damaged);
        problems.AddRange(damaged.Select(d => $"Version file {d} can't be read."));
        var id = snapshotId ?? snapshots.FirstOrDefault()?.Id;
        if (id is null) return new(0, [.. problems, "There's no backup to check yet."], watch.Elapsed);
        var entries = ReadSnapshot(destination, key, id);
        var scratch = Path.Combine(Path.GetTempPath(), "keepsake-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var checkedCount = 0;
        try
        {
            // Each distinct object once: identical files share one.
            var distinct = entries.GroupBy(e => e.Object).Select(g => g.First()).ToList();
            for (var i = 0; i < distinct.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = distinct[i];
                if (i % 10 == 0) progress?.Report(new(i, distinct.Count, e.Path));
                var tmp = Path.Combine(scratch, "f" + TempSuffix);
                try { ReadObject(destination, key, e, tmp, ct); checkedCount++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException or InvalidDataException or JsonException)
                {
                    problems.Add($"{e.Path}: {ex.Message}");
                }
                finally { try { File.Delete(tmp); } catch (IOException) { } }
            }
            progress?.Report(new(distinct.Count, distinct.Count, null));
        }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } }
        return new(checkedCount, problems, watch.Elapsed);
    }
}
