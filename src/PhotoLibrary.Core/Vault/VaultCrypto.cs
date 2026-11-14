using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PhotoLibrary.Core.Vault;

/// <summary>Argon2id cost settings, stored in each vault's key file so they can be raised later.</summary>
public sealed record KdfParams(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>64 MiB, 3 passes, 4 lanes: about half a second per unlock, well above OWASP's minimum.</summary>
    public static readonly KdfParams Default = new(64 * 1024, 3, 4);

    public string Describe() => $"Argon2id, {MemoryKiB / 1024:N0} MiB, {Iterations} passes, {Parallelism} lanes";
}

/// <summary>A named Argon2id strength offered in Settings.</summary>
public sealed record KdfPreset(string Id, string Name, string Hint, KdfParams Params)
{
    public static readonly KdfPreset[] All =
    [
        new("standard", "Standard", "64 MiB, 3 passes. About half a second to unlock on a typical PC.", KdfParams.Default),
        new("strong", "Strong", "256 MiB, 3 passes. Around 2 seconds to unlock; four times the work for anyone guessing passwords.", new KdfParams(256 * 1024, 3, 4)),
        new("maximum", "Maximum", "1 GiB, 4 passes. Several seconds to unlock and needs 1 GiB of free memory while it does.", new KdfParams(1024 * 1024, 4, 4)),
    ];

    public static KdfPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    /// <summary>The preset with exactly these settings, if any.</summary>
    public static KdfPreset? Matching(KdfParams p) => All.FirstOrDefault(x => x.Params == p);
}

/// <summary>Thrown when a password is wrong or a vault file has been altered or truncated.</summary>
public sealed class VaultAuthenticationException(string message) : Exception(message);

/// <summary>
/// The cryptography behind vaults, built only on AES-256-GCM (in .NET), HKDF-SHA256 (in .NET) and Argon2id.
/// <para>
/// Key file: the password goes through Argon2id with a random salt to a key-encryption key, which unwraps a random
/// 32-byte vault key (AES-GCM, with the vault id as associated data). Changing the password re-wraps that key.
/// </para>
/// <para>
/// Each encrypted file: magic, version, a random 16-byte file salt, then sealed records. A file key is derived
/// from the vault key and the salt with HKDF, so no two files share a key and counter nonces are safe. Record 0
/// holds the metadata (original name, dates, size...), the rest hold the content in 1 MiB chunks. Each record's
/// nonce is its index, with a final-record flag, so reordering, dropping or truncating records is detected
/// (the STREAM construction). Nothing about the photo is readable without the vault key.
/// </para>
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int ChunkSize = 1 << 20;
    private const int TagSize = 16, NonceSize = 12, SaltSize = 16;
    private static readonly byte[] Magic = "PLVAULT1"u8.ToArray();
    private const byte FileVersion = 1;
    public const int FileHeaderSize = 8 + 1 + SaltSize;

    // ---- password → key

    public static byte[] DeriveKek(string password, byte[] salt, KdfParams p)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(pw) { Salt = salt, MemorySize = p.MemoryKiB, Iterations = p.Iterations, DegreeOfParallelism = p.Parallelism };
            return argon.GetBytes(KeySize);
        }
        finally { CryptographicOperations.ZeroMemory(pw); }
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>nonce ‖ ciphertext ‖ tag of the vault key under the key-encryption key.</summary>
    public static byte[] WrapKey(byte[] kek, byte[] vaultKey, string vaultId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[NonceSize + vaultKey.Length + TagSize];
        nonce.CopyTo(output, 0);
        using var gcm = new AesGcm(kek, TagSize);
        gcm.Encrypt(nonce, vaultKey, output.AsSpan(NonceSize, vaultKey.Length), output.AsSpan(NonceSize + vaultKey.Length), Aad(vaultId));
        return output;
    }

    public static byte[] UnwrapKey(byte[] kek, byte[] wrapped, string vaultId) =>
        TryUnwrapKey(kek, wrapped, vaultId) ?? throw new VaultAuthenticationException(WrongPassword);

    public const string WrongPassword = "Wrong password.";

    /// <summary>
    /// The vault key, or null when <paramref name="kek"/> came from the wrong password. A mistyped password is
    /// ordinary input, so it isn't an exception; a damaged key file still is.
    /// </summary>
    public static byte[]? TryUnwrapKey(byte[] kek, byte[] wrapped, string vaultId)
    {
        if (wrapped.Length != NonceSize + KeySize + TagSize) throw new VaultAuthenticationException("The vault key file is damaged.");
        var key = new byte[KeySize];
        using var gcm = new AesGcm(kek, TagSize);
        try { gcm.Decrypt(wrapped.AsSpan(0, NonceSize), wrapped.AsSpan(NonceSize, KeySize), wrapped.AsSpan(NonceSize + KeySize), key, Aad(vaultId)); }
        catch (AuthenticationTagMismatchException) { return null; }
        return key;
    }

    private static byte[] Aad(string vaultId) => Encoding.UTF8.GetBytes("photolibrary-vault-key:" + vaultId);

    // ---- files

    private static byte[] FileKey(byte[] vaultKey, ReadOnlySpan<byte> salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, KeySize, salt.ToArray(), "photolibrary-vault-file"u8.ToArray());

    private static void Nonce(Span<byte> nonce, long index, bool final)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[3..11], index);
        nonce[11] = final ? (byte)1 : (byte)0;
    }

    /// <summary>Encrypts <paramref name="content"/> with its metadata into <paramref name="output"/>.</summary>
    public static void EncryptFile(byte[] vaultKey, byte[] metadata, Stream content, Stream output)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = FileKey(vaultKey, salt);
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            output.Write(Magic);
            output.WriteByte(FileVersion);
            output.Write(salt);
            Span<byte> nonce = stackalloc byte[NonceSize];

            // Record 0: metadata, length-prefixed.
            Nonce(nonce, 0, false);
            var sealedMeta = new byte[metadata.Length + TagSize];
            gcm.Encrypt(nonce, metadata, sealedMeta.AsSpan(0, metadata.Length), sealedMeta.AsSpan(metadata.Length));
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, metadata.Length);
            output.Write(len);
            output.Write(sealedMeta);

            // Content chunks; read one ahead so the last one can be flagged final (an empty file is one empty final chunk).
            var current = new byte[ChunkSize];
            var next = new byte[ChunkSize];
            var cipher = new byte[ChunkSize + TagSize];
            var n = ReadFull(content, current);
            long index = 1;
            while (true)
            {
                var m = n == ChunkSize ? ReadFull(content, next) : 0;
                var final = m == 0;
                Nonce(nonce, index++, final);
                gcm.Encrypt(nonce, current.AsSpan(0, n), cipher.AsSpan(0, n), cipher.AsSpan(n, TagSize));
                output.Write(cipher, 0, n + TagSize);
                if (final) break;
                (current, next) = (next, current);
                n = m;
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Reads only the metadata record (cheap: used to list a vault).</summary>
    public static byte[] ReadMetadata(byte[] vaultKey, Stream input)
    {
        var (gcm, key) = OpenHeader(vaultKey, input);
        try
        {
            using (gcm) return ReadMeta(gcm, input);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts the whole file: its metadata and content. Throws if anything was altered or cut short.</summary>
    public static byte[] DecryptFile(byte[] vaultKey, Stream input, Stream content)
    {
        var (gcm, key) = OpenHeader(vaultKey, input);
        try
        {
            using (gcm)
            {
                var meta = ReadMeta(gcm, input);
                var cipher = new byte[ChunkSize + TagSize];
                var plain = new byte[ChunkSize];
                Span<byte> nonce = stackalloc byte[NonceSize];
                long index = 1;
                while (true)
                {
                    var n = ReadFull(input, cipher);
                    if (n < TagSize) throw new VaultAuthenticationException("The file is truncated.");
                    var final = n < cipher.Length || input.Position == input.Length;
                    Nonce(nonce, index++, final);
                    try { gcm.Decrypt(nonce, cipher.AsSpan(0, n - TagSize), cipher.AsSpan(n - TagSize, TagSize), plain.AsSpan(0, n - TagSize)); }
                    catch (AuthenticationTagMismatchException) { throw new VaultAuthenticationException("The file has been altered or truncated."); }
                    content.Write(plain, 0, n - TagSize);
                    if (final) break;
                }
                CryptographicOperations.ZeroMemory(plain);
                return meta;
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static (AesGcm Gcm, byte[] Key) OpenHeader(byte[] vaultKey, Stream input)
    {
        var header = new byte[FileHeaderSize];
        if (ReadFull(input, header) != FileHeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new VaultAuthenticationException("Not a vault file.");
        if (header[8] != FileVersion) throw new VaultAuthenticationException($"Unsupported vault file version {header[8]}.");
        var key = FileKey(vaultKey, header.AsSpan(9, SaltSize));
        return (new AesGcm(key, TagSize), key);
    }

    private static byte[] ReadMeta(AesGcm gcm, Stream input)
    {
        var len = new byte[4];
        if (ReadFull(input, len) != 4) throw new VaultAuthenticationException("The file is truncated.");
        var n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n < 0 || n > 1 << 20) throw new VaultAuthenticationException("The file is damaged.");
        var sealedMeta = new byte[n + TagSize];
        if (ReadFull(input, sealedMeta) != sealedMeta.Length) throw new VaultAuthenticationException("The file is truncated.");
        var meta = new byte[n];
        Span<byte> nonce = stackalloc byte[NonceSize];
        Nonce(nonce, 0, false);
        try { gcm.Decrypt(nonce, sealedMeta.AsSpan(0, n), sealedMeta.AsSpan(n), meta); }
        catch (AuthenticationTagMismatchException) { throw new VaultAuthenticationException("Wrong key, or the file has been altered."); }
        return meta;
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = s.Read(buffer, got, buffer.Length - got);
            if (n == 0) break;
            got += n;
        }
        return got;
    }
}
