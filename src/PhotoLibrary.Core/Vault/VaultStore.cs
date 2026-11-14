using System.Security.Cryptography;
using System.Text.Json;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.Core.Vault;

/// <summary>What a vault remembers about a photo, kept encrypted inside the photo's own vault file.</summary>
public sealed record VaultItemInfo
{
    public required string OriginalName { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public MediaKind Kind { get; init; }
    public DateTime? Taken { get; init; }
    public string? TakenSource { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Orientation { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public long? DurationMs { get; init; }
    public string? Description { get; init; }
    public DateTime AddedToVault { get; init; }

    public string Extension => Path.GetExtension(OriginalName);

    public static VaultItemInfo From(MediaRecord r) => new()
    {
        OriginalName = r.FileName, Size = r.Size, Sha256 = r.Sha256, Kind = r.Kind, Taken = r.Taken, TakenSource = r.TakenSource,
        Make = r.Make, Model = r.Model, Width = r.Width, Height = r.Height, Orientation = r.Orientation, Lat = r.Lat, Lon = r.Lon,
        DurationMs = r.DurationMs, Description = r.Description, AddedToVault = DateTime.Now,
    };
}

/// <summary>One encrypted file in a vault and its decrypted metadata.</summary>
public sealed record VaultItem(string FilePath, VaultItemInfo Info)
{
    public string Id => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>The contents of <c>vault.json</c>: everything needed to unlock, nothing about the photos.</summary>
public sealed record VaultKeyFile(string Format, int Version, string VaultId, string Kdf, int MemoryKiB, int Iterations, int Parallelism, string Salt, string WrappedKey);

/// <summary>
/// A vault is a folder (<c>&lt;library&gt;\_Vault\&lt;random id&gt;</c>) holding <c>vault.json</c> and one
/// <c>&lt;random&gt;.plv</c> file per photo. File names, dates, camera, GPS and pixels are all inside the
/// encrypted files, so the folder reveals only how many items there are and roughly how big.
/// </summary>
public static class VaultStore
{
    public const string KeyFileName = "vault.json";
    public const string ItemExtension = ".plv";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string VaultsDir(string libraryRoot) => Path.Combine(libraryRoot, LibraryFolders.Vault);
    public static string FolderFor(string libraryRoot, string vaultId) => Path.Combine(VaultsDir(libraryRoot), vaultId);

    /// <summary>Creates a vault folder and key file. Returns the new vault's id.</summary>
    public static string Create(string libraryRoot, string password, KdfParams? kdf = null)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var dir = FolderFor(libraryRoot, id);
        Directory.CreateDirectory(dir);
        var key = VaultCrypto.NewKey();
        try { WriteKeyFile(dir, id, password, key, kdf ?? KdfParams.Default); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return id;
    }

    private static void WriteKeyFile(string dir, string id, string password, byte[] vaultKey, KdfParams p)
    {
        var salt = VaultCrypto.NewSalt();
        var kek = VaultCrypto.DeriveKek(password, salt, p);
        try
        {
            var file = new VaultKeyFile("photolibrary-vault", 1, id, "argon2id", p.MemoryKiB, p.Iterations, p.Parallelism,
                Convert.ToBase64String(salt), Convert.ToBase64String(VaultCrypto.WrapKey(kek, vaultKey, id)));
            var path = Path.Combine(dir, KeyFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static VaultKeyFile ReadKeyFile(string dir) =>
        JsonSerializer.Deserialize<VaultKeyFile>(File.ReadAllText(Path.Combine(dir, KeyFileName)))
        ?? throw new VaultAuthenticationException("The vault key file is damaged.");

    /// <summary>Checks the password and returns the vault key. Throws <see cref="VaultAuthenticationException"/> if it's wrong.</summary>
    public static byte[] Unlock(string dir, string password) =>
        TryUnlock(dir, password) ?? throw new VaultAuthenticationException(VaultCrypto.WrongPassword);

    /// <summary>The vault key, or null when the password is wrong. A damaged or unreadable key file still throws.</summary>
    public static byte[]? TryUnlock(string dir, string password)
    {
        var kf = ReadKeyFile(dir);
        if (kf.Kdf != "argon2id") throw new VaultAuthenticationException($"Unknown key derivation “{kf.Kdf}”.");
        var kek = VaultCrypto.DeriveKek(password, Convert.FromBase64String(kf.Salt), new KdfParams(kf.MemoryKiB, kf.Iterations, kf.Parallelism));
        try { return VaultCrypto.TryUnwrapKey(kek, Convert.FromBase64String(kf.WrappedKey), kf.VaultId); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Re-wraps the same vault key under a new password; no photo is re-encrypted.</summary>
    public static void ChangePassword(string dir, string oldPassword, string newPassword, KdfParams? kdf = null)
    {
        if (!TryChangePassword(dir, oldPassword, newPassword, kdf)) throw new VaultAuthenticationException(VaultCrypto.WrongPassword);
    }

    /// <summary>As <see cref="ChangePassword"/>, but returns false (changing nothing) when the old password is wrong.</summary>
    public static bool TryChangePassword(string dir, string oldPassword, string newPassword, KdfParams? kdf = null)
    {
        if (TryUnlock(dir, oldPassword) is not { } key) return false;
        try { WriteKeyFile(dir, ReadKeyFile(dir).VaultId, newPassword, key, kdf ?? KdfParams.Default); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return true;
    }

    public static int CountItems(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*" + ItemExtension).Count() : 0;

    /// <summary>Decrypts every item's metadata (not the pixels). Damaged files are skipped and reported.</summary>
    public static List<VaultItem> List(string dir, byte[] key, List<string>? damaged = null)
    {
        var items = new List<VaultItem>();
        foreach (var f in Directory.EnumerateFiles(dir, "*" + ItemExtension))
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                var info = JsonSerializer.Deserialize<VaultItemInfo>(VaultCrypto.ReadMetadata(key, fs));
                if (info is not null) items.Add(new VaultItem(f, info));
            }
            catch (Exception ex) when (ex is VaultAuthenticationException or JsonException or IOException)
            {
                damaged?.Add(Path.GetFileName(f));
            }
        }
        return items.OrderBy(i => i.Info.Taken ?? DateTime.MaxValue).ThenBy(i => i.Info.OriginalName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Encrypts a photo into the vault and proves the result decrypts back to the same bytes (SHA-256) before
    /// returning. The caller deletes the original only after this succeeds.
    /// </summary>
    public static VaultItem Add(string dir, byte[] key, string sourcePath, VaultItemInfo info)
    {
        var target = Path.Combine(dir, Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + ItemExtension);
        var tmp = target + ".part";
        try
        {
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                VaultCrypto.EncryptFile(key, JsonSerializer.SerializeToUtf8Bytes(info), src, dst);
                dst.Flush(flushToDisk: true);
            }
            using (var check = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var hash = new HashingStream())
            {
                VaultCrypto.DecryptFile(key, check, hash);
                if (!string.Equals(hash.HexDigest(), info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{info.OriginalName} changed while it was being encrypted; the original was left in place.");
            }
            File.Move(tmp, target);
            return new VaultItem(target, info);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The decrypted file, in memory only.</summary>
    public static byte[] ReadContent(VaultItem item, byte[] key)
    {
        using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var ms = new MemoryStream((int)Math.Min(item.Info.Size, int.MaxValue));
        VaultCrypto.DecryptFile(key, fs, ms);
        return ms.ToArray();
    }

    /// <summary>Writes a decrypted copy to <paramref name="destination"/> (which must not exist). The vault keeps its copy.</summary>
    public static void Export(VaultItem item, byte[] key, string destination)
    {
        var tmp = destination + ".part";
        try
        {
            using (var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                VaultCrypto.DecryptFile(key, fs, dst);
            if (item.Info.Taken is { } t) File.SetLastWriteTime(tmp, t);
            File.Move(tmp, destination);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    public static void Delete(VaultItem item) => File.Delete(item.FilePath);

    /// <summary>A write-only stream that just hashes, for verifying an encryption without a plaintext copy on disk.</summary>
    private sealed class HashingStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string HexDigest() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
