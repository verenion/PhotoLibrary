using System.Security.Cryptography;
using System.Text;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Vault;
using SkiaSharp;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Vault encryption: round trips, wrong passwords, tampering, nothing readable on disk, and index cleanup.</summary>
public class VaultTests : IDisposable
{
    // Cheap Argon2id settings so the tests run fast; the format stores whatever parameters were used.
    private static readonly KdfParams Fast = new(1024, 1, 1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-vault-" + Guid.NewGuid().ToString("N"));

    public VaultTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string WriteFile(string name, byte[] data)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    private static VaultItemInfo Info(string name, byte[] data) =>
        new() { OriginalName = name, Size = data.Length, Sha256 = Sha(data), Kind = MediaKind.Image, Taken = new DateTime(2020, 1, 2, 3, 4, 5), Lat = 51.5, AddedToVault = DateTime.Now };

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(VaultCrypto.ChunkSize)]          // exactly one chunk
    [InlineData(VaultCrypto.ChunkSize * 2 + 7)]  // several chunks and a partial one
    public void FilesRoundTripAtEverySize(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        var src = WriteFile("in.bin", data);
        var id = VaultStore.Create(_root, "correct horse", Fast);
        var dir = VaultStore.FolderFor(_root, id);
        var key = VaultStore.Unlock(dir, "correct horse");

        var item = VaultStore.Add(dir, key, src, Info("secret scan.jpg", data));
        Assert.Equal(data, VaultStore.ReadContent(item, key));
        var listed = Assert.Single(VaultStore.List(dir, key));
        Assert.Equal("secret scan.jpg", listed.Info.OriginalName);
        Assert.Equal(51.5, listed.Info.Lat);
    }

    [Fact]
    public void NothingAboutThePhotoIsReadableOnDisk()
    {
        var data = Encoding.ASCII.GetBytes(new string('A', 5000) + "PASSPORT-NUMBER-123");
        var src = WriteFile("passport scan.jpg", data);
        var id = VaultStore.Create(_root, "pw-123456", Fast);
        var dir = VaultStore.FolderFor(_root, id);
        var key = VaultStore.Unlock(dir, "pw-123456");
        var item = VaultStore.Add(dir, key, src, Info("passport scan.jpg", data));

        var onDisk = File.ReadAllBytes(item.FilePath);
        var text = Encoding.Latin1.GetString(onDisk);
        Assert.DoesNotContain("passport", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSPORT-NUMBER", text);
        Assert.DoesNotContain("AAAAAAAA", text);
        Assert.DoesNotContain("2020", text);
        Assert.DoesNotContain("passport", Path.GetFileName(item.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pw-123456", File.ReadAllText(Path.Combine(dir, VaultStore.KeyFileName)));
    }

    [Fact]
    public void WrongPasswordAndOtherVaultsKeysAreRejected()
    {
        var a = VaultStore.FolderFor(_root, VaultStore.Create(_root, "alpha-pass", Fast));
        var b = VaultStore.FolderFor(_root, VaultStore.Create(_root, "bravo-pass", Fast));
        Assert.Throws<VaultAuthenticationException>(() => VaultStore.Unlock(a, "bravo-pass"));

        var data = RandomNumberGenerator.GetBytes(100);
        var item = VaultStore.Add(a, VaultStore.Unlock(a, "alpha-pass"), WriteFile("x.jpg", data), Info("x.jpg", data));
        Assert.Throws<VaultAuthenticationException>(() => VaultStore.ReadContent(item, VaultStore.Unlock(b, "bravo-pass")));
    }

    [Fact]
    public void TamperingAndTruncationAreDetected()
    {
        var data = RandomNumberGenerator.GetBytes(VaultCrypto.ChunkSize + 500);
        var dir = VaultStore.FolderFor(_root, VaultStore.Create(_root, "pw-tamper", Fast));
        var key = VaultStore.Unlock(dir, "pw-tamper");
        var item = VaultStore.Add(dir, key, WriteFile("t.jpg", data), Info("t.jpg", data));
        var good = File.ReadAllBytes(item.FilePath);

        var flipped = (byte[])good.Clone();
        flipped[^100] ^= 1;
        File.WriteAllBytes(item.FilePath, flipped);
        Assert.Throws<VaultAuthenticationException>(() => VaultStore.ReadContent(item, key));

        // Drop the final chunk: the one before it was not sealed as final, so this must fail too.
        File.WriteAllBytes(item.FilePath, good[..^(500 + 16)]);
        Assert.Throws<VaultAuthenticationException>(() => VaultStore.ReadContent(item, key));
    }

    [Fact]
    public void ChangingThePasswordKeepsTheFiles()
    {
        var data = RandomNumberGenerator.GetBytes(300);
        var dir = VaultStore.FolderFor(_root, VaultStore.Create(_root, "old-password", Fast));
        var item = VaultStore.Add(dir, VaultStore.Unlock(dir, "old-password"), WriteFile("c.jpg", data), Info("c.jpg", data));

        Assert.Throws<VaultAuthenticationException>(() => VaultStore.ChangePassword(dir, "not-it", "new-password", Fast));
        VaultStore.ChangePassword(dir, "old-password", "new-password", Fast);
        Assert.Throws<VaultAuthenticationException>(() => VaultStore.Unlock(dir, "old-password"));
        Assert.Equal(data, VaultStore.ReadContent(item, VaultStore.Unlock(dir, "new-password")));
    }

    [Fact]
    public void AWrongPasswordIsAnAnswerNotAnException()
    {
        var dir = VaultStore.FolderFor(_root, VaultStore.Create(_root, "right-password", Fast));
        Assert.Null(VaultStore.TryUnlock(dir, "wrong-password"));
        Assert.NotNull(VaultStore.TryUnlock(dir, "right-password"));
        Assert.False(VaultStore.TryChangePassword(dir, "wrong-password", "new-password", Fast));
        Assert.NotNull(VaultStore.TryUnlock(dir, "right-password")); // nothing changed

        File.WriteAllText(Path.Combine(dir, VaultStore.KeyFileName), "{}"); // damage still throws
        Assert.ThrowsAny<Exception>(() => VaultStore.TryUnlock(dir, "right-password"));
    }

    [Fact]
    public void ExportWritesADecryptedCopy()
    {
        var data = RandomNumberGenerator.GetBytes(2000);
        var dir = VaultStore.FolderFor(_root, VaultStore.Create(_root, "pw-export", Fast));
        var key = VaultStore.Unlock(dir, "pw-export");
        var item = VaultStore.Add(dir, key, WriteFile("e.jpg", data), Info("e.jpg", data));
        var dest = Path.Combine(_root, "out", "e.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        VaultStore.Export(item, key, dest);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), File.GetLastWriteTime(dest));
    }

    [Fact]
    public void ImagesDecodeFromMemory()
    {
        using var bmp = new SKBitmap(64, 32);
        bmp.Erase(SKColors.Red);
        var jpeg = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        using var decoded = PhotoLibrary.Core.Imaging.ImageDecoder.DecodeInMemory(jpeg, "doc.jpg", MediaKind.Image, 32);
        Assert.NotNull(decoded);
        Assert.Equal(32, decoded!.Width);
        Assert.Null(PhotoLibrary.Core.Imaging.ImageDecoder.DecodeInMemory(jpeg, "clip.mp4", MediaKind.Video, 32));
    }

    [Fact]
    public void ForgettingFilesRemovesEveryDatabaseTrace()
    {
        using var db = new LibraryDb(Path.Combine(_root, "t.sqlite"));
        var r = new MediaRecord { Path = @"C:\lib\passport.jpg", Sha256 = new string('a', 64), Size = 1, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Added = DateTime.Now };
        db.Upsert(r);
        var album = db.CreateAlbum("Docs", PhotoLibrary.Core.Albums.AlbumKind.Virtual);
        db.AddToAlbum(album.Id, [r.Id]);
        var run = db.BeginImportRun(@"C:\in");
        db.AddImportItems(run, [(@"C:\in\passport.jpg", "missing", r.Path, "copied")]);
        album.CoverFileId = r.Id;
        db.UpdateAlbum(album);

        db.ForgetFiles([r]);

        Assert.Null(db.GetByPath(r.Path));
        Assert.Empty(db.GetAlbumItemIds(album.Id));
        Assert.Null(db.GetAlbum(album.Id)!.CoverFileId);
        Assert.Equal(0L, db.Run(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM import_items WHERE library_path LIKE '%passport%'"; return (long)cmd.ExecuteScalar()!; }));

        var secure = db.CreateAlbum("Vault", PhotoLibrary.Core.Albums.AlbumKind.Secure, vaultId: "abc123");
        var back = db.GetAlbum(secure.Id)!;
        Assert.True(back.IsSecure);
        Assert.Equal("abc123", back.VaultId);
    }
    [Fact]
    public void SecureAlbumMovesPhotosInAndDeletingItRestoresThem()
    {
        var lib = Path.Combine(_root, "Library");
        var day = new DateTime(2021, 6, 15, 10, 0, 0);
        var dated = Path.Combine(lib, DateRules.DatedFolder(day));
        Directory.CreateDirectory(dated);
        var data = RandomNumberGenerator.GetBytes(4000);
        var path = Path.Combine(dated, "passport.jpg");
        File.WriteAllBytes(path, data);
        using var db = new LibraryDb(Path.Combine(_root, "lib.sqlite"));
        db.AddDateFolders(["2021"]);
        var rec = new MediaRecord { Path = path, Sha256 = Sha(data), Size = data.Length, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = day, Added = DateTime.Now };
        db.Upsert(rec);
        var albums = new PhotoLibrary.Core.Albums.AlbumService(db, lib);
        var docs = albums.Create("Docs", PhotoLibrary.Core.Albums.AlbumKind.Virtual);
        db.AddToAlbum(docs.Id, [rec.Id]);

        var vault = albums.Create("Private", PhotoLibrary.Core.Albums.AlbumKind.Secure, password: "vault-pass-1");
        Assert.True(vault.IsSecure);
        var key = VaultStore.Unlock(albums.FolderFor(vault), "vault-pass-1");
        var change = albums.AddToVault(vault, key, [rec]);

        Assert.Single(change.Vaulted);
        Assert.Empty(change.Errors);
        Assert.False(File.Exists(path));                       // original gone
        Assert.Null(db.GetByPath(path));                       // row gone
        Assert.Empty(db.GetAlbumItemIds(docs.Id));             // other memberships gone
        Assert.Equal(1, VaultStore.CountItems(albums.FolderFor(vault)));

        // The scanner never looks inside _Vault.
        Assert.Contains(LibraryFolders.Vault, LibraryFolders.ExcludedFromIndex);

        var (restored, errors) = albums.DeleteVault(vault, key);
        Assert.Empty(errors);
        Assert.Equal(path, Assert.Single(restored));
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Null(db.GetAlbum(vault.Id));
        Assert.False(Directory.Exists(albums.FolderFor(vault)));
    }

    [Fact]
    public void AVaultFolderWithNoAlbumIsAdopted()
    {
        var lib = Path.Combine(_root, "Library2");
        Directory.CreateDirectory(lib);
        var id = VaultStore.Create(lib, "orphan-pass", Fast);
        using var db = new LibraryDb(Path.Combine(_root, "lib2.sqlite"));
        var albums = new PhotoLibrary.Core.Albums.AlbumService(db, lib);
        Assert.True(albums.SyncFolders([]));
        var adopted = Assert.Single(db.GetAlbums());
        Assert.True(adopted.IsSecure);
        Assert.Equal(id, adopted.VaultId);
        Assert.False(albums.SyncFolders([]));
    }

    [Fact]
    public void ChangingThePasswordAppliesNewStrengthWithoutTouchingFiles()
    {
        var lib = Path.Combine(_root, "Library3");
        Directory.CreateDirectory(lib);
        var id = VaultStore.Create(lib, "first-pass-1", Fast);
        var dir = VaultStore.FolderFor(lib, id);
        var src = Path.Combine(_root, "doc.jpg");
        File.WriteAllBytes(src, [1, 2, 3, 4]);
        var key = VaultStore.Unlock(dir, "first-pass-1");
        var item = VaultStore.Add(dir, key, src, new VaultItemInfo { OriginalName = "doc.jpg", Size = 4, Sha256 = Sha([1, 2, 3, 4]) });
        var before = File.ReadAllBytes(item.FilePath);

        var stronger = Fast with { Iterations = Fast.Iterations + 1 };
        VaultStore.ChangePassword(dir, "first-pass-1", "first-pass-1", stronger);   // same password, new strength

        var kf = VaultStore.ReadKeyFile(dir);
        Assert.Equal(stronger, new KdfParams(kf.MemoryKiB, kf.Iterations, kf.Parallelism));
        Assert.Equal(before, File.ReadAllBytes(item.FilePath));                    // photos not re-encrypted
        Assert.Equal(key, VaultStore.Unlock(dir, "first-pass-1"));                // same vault key underneath
        Assert.Contains(KdfPreset.All, p => p.Params == KdfParams.Default);
        Assert.Equal("standard", KdfPreset.Find("nonsense").Id);
    }
}
