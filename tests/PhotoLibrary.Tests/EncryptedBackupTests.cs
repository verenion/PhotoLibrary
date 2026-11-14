using System.Security.Cryptography;
using System.Text;
using PhotoLibrary.Core.Backup;
using PhotoLibrary.Core.Vault;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>Encrypted backups: unreadable at rest, incremental, versioned, restorable and checkable.</summary>
public class EncryptedBackupTests : IDisposable
{
    private static readonly KdfParams Fast = new(1024, 1, 1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-ebackup-" + Guid.NewGuid().ToString("N"));
    private string Lib => Path.Combine(_root, "Library");
    private string Dest => Path.Combine(_root, "Backup");
    private readonly byte[] _photo = RandomNumberGenerator.GetBytes(300_000); // incompressible, like a JPEG
    private const string Notes = "Holiday notes: the secret beach is past the lighthouse. ";

    public EncryptedBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Lib, "2012", "2012_05"));
        Directory.CreateDirectory(Path.Combine(Lib, "Camera Roll"));
        Directory.CreateDirectory(Dest);
        File.WriteAllBytes(Path.Combine(Lib, "2012", "2012_05", "IMG_1.jpg"), _photo);
        File.WriteAllBytes(Path.Combine(Lib, "Camera Roll", "IMG_1 copy.jpg"), _photo); // identical content
        File.WriteAllText(Path.Combine(Lib, "notes.txt"), string.Concat(Enumerable.Repeat(Notes, 400)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private byte[] NewBackup(out string recovery)
    {
        var (key, r) = EncryptedBackup.Create(Dest, "correct horse", Fast);
        recovery = r;
        return key;
    }

    [Fact]
    public void BacksUpUnreadablyAndRestoresExactly()
    {
        var key = NewBackup(out _);
        var r = EncryptedBackup.Run(Lib, Dest, key, null, keepFor: null);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(2, r.Copied);     // two distinct contents
        Assert.Equal(1, r.Unchanged);  // the copy reuses the first object

        // Nothing in the backup folder names a file or contains its text.
        var everything = Directory.EnumerateFiles(Dest, "*", SearchOption.AllDirectories).ToList();
        Assert.DoesNotContain(everything, p => p.Contains("IMG_1") || p.Contains("notes") || p.Contains("Camera Roll"));
        Assert.DoesNotContain(everything, p => Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains("lighthouse"));
        // The text compressed well; the random "photo" was stored as it was.
        var objects = Directory.EnumerateFiles(Path.Combine(Dest, EncryptedBackup.ObjectsDir), "*", SearchOption.AllDirectories).Select(p => new FileInfo(p).Length).ToList();
        Assert.Equal(2, objects.Count);
        Assert.Contains(objects, n => n < 2000);

        var target = Path.Combine(_root, "Restored");
        var snapshot = Assert.Single(EncryptedBackup.ListSnapshots(Dest, key));
        Assert.Equal(3, snapshot.Files);
        var restored = EncryptedBackup.Restore(Dest, key, snapshot.Id, target, null);
        Assert.True(restored.Ok, string.Join("; ", restored.Errors));
        Assert.Equal(_photo, File.ReadAllBytes(Path.Combine(target, "Camera Roll", "IMG_1 copy.jpg")));
        Assert.Equal(File.ReadAllText(Path.Combine(Lib, "notes.txt")), File.ReadAllText(Path.Combine(target, "notes.txt")));

        // Restoring again never overwrites.
        Assert.Equal(3, EncryptedBackup.Restore(Dest, key, snapshot.Id, target, null).Skipped);
    }

    [Fact]
    public void PasswordAndRecoveryKeyBothUnlock()
    {
        var key = NewBackup(out var recovery);
        Assert.Matches("^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$", recovery);
        Assert.Null(EncryptedBackup.TryUnlock(Dest, "wrong"));
        Assert.Equal(key, EncryptedBackup.TryUnlock(Dest, "correct horse"));
        Assert.Equal(key, EncryptedBackup.TryUnlock(Dest, recovery.ToLowerInvariant().Replace("-", " ")));

        // Forgot the password: the recovery key sets a new one, and still works afterwards.
        Assert.True(EncryptedBackup.TryChangePassword(Dest, recovery, "new password"));
        Assert.Null(EncryptedBackup.TryUnlock(Dest, "correct horse"));
        Assert.Equal(key, EncryptedBackup.TryUnlock(Dest, "new password"));
        Assert.Equal(key, EncryptedBackup.TryUnlock(Dest, recovery));
    }

    [Fact]
    public void UnchangedFilesArentReadAgainAndDeletedOnesStayInOlderVersions()
    {
        var key = NewBackup(out _);
        EncryptedBackup.Run(Lib, Dest, key, null, keepFor: null);
        var second = EncryptedBackup.Run(Lib, Dest, key, null, keepFor: null);
        Assert.Equal(0, second.Copied + second.Updated);
        Assert.Equal(3, second.Unchanged);

        File.Delete(Path.Combine(Lib, "notes.txt"));
        var third = EncryptedBackup.Run(Lib, Dest, key, null, keepFor: null);
        Assert.Equal(1, third.Archived);
        var versions = EncryptedBackup.ListSnapshots(Dest, key);
        Assert.Equal(3, versions.Count);
        Assert.DoesNotContain(EncryptedBackup.ReadSnapshot(Dest, key, versions[0].Id), e => e.Path == "notes.txt");

        var target = Path.Combine(_root, "Older");
        var restored = EncryptedBackup.Restore(Dest, key, versions[^1].Id, target, p => p.EndsWith(".txt", StringComparison.Ordinal));
        Assert.Equal(1, restored.Restored);
        Assert.True(File.Exists(Path.Combine(target, "notes.txt")));

        // Keeping nothing old drops the older versions and the file only they used.
        var fourth = EncryptedBackup.Run(Lib, Dest, key, null, keepFor: TimeSpan.Zero);
        Assert.Equal(3, fourth.Expired);
        Assert.Single(EncryptedBackup.ListSnapshots(Dest, key));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Dest, EncryptedBackup.ObjectsDir), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void VerifyFindsDamage()
    {
        var key = NewBackup(out _);
        EncryptedBackup.Run(Lib, Dest, key, null, keepFor: null);
        Assert.True(EncryptedBackup.Verify(Dest, key).Ok);

        var victim = Directory.EnumerateFiles(Path.Combine(Dest, EncryptedBackup.ObjectsDir), "*", SearchOption.AllDirectories).First();
        var bytes = File.ReadAllBytes(victim);
        bytes[^20] ^= 0xFF;
        File.WriteAllBytes(victim, bytes);
        var check = EncryptedBackup.Verify(Dest, key);
        Assert.False(check.Ok);
        Assert.Single(check.Problems);
    }

    [Fact]
    public void FormatsDontShareAFolder()
    {
        File.WriteAllText(Path.Combine(Dest, "something.txt"), "x");
        Assert.NotNull(EncryptedBackup.Validate(Lib, Dest));        // not empty
        File.Delete(Path.Combine(Dest, "something.txt"));
        Assert.Null(EncryptedBackup.Validate(Lib, Dest));
        NewBackup(out _);
        Assert.Null(EncryptedBackup.Validate(Lib, Dest));           // reconnecting to it is fine
        Assert.NotNull(BackupService.ValidatePlain(Lib, Dest));     // but a plain copy can't go in it
    }

    [Fact]
    public void TheDatabaseSnapshotIsIncluded()
    {
        var key = NewBackup(out _);
        var r = EncryptedBackup.Run(Lib, Dest, key, path => File.WriteAllText(path, "db"), keepFor: null);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var snapshot = EncryptedBackup.ListSnapshots(Dest, key)[0];
        Assert.Contains(EncryptedBackup.ReadSnapshot(Dest, key, snapshot.Id), e => e.Path == Path.Combine(BackupService.AppDataFolder, "library.sqlite"));
    }
}

public class KeyProtectorTests
{
    [Fact]
    public void RoundTripsForThisUserOnly()
    {
        if (!KeyProtector.IsAvailable) return; // DPAPI is Windows-only
        var secret = RandomNumberGenerator.GetBytes(32);
        var blob = KeyProtector.Protect(secret);
        Assert.NotNull(blob);
        Assert.DoesNotContain(Convert.ToBase64String(secret), blob);
        Assert.Equal(secret, KeyProtector.Unprotect(blob!));
        var tampered = Convert.FromBase64String(blob!);
        tampered[^5] ^= 0xFF;
        Assert.Null(KeyProtector.Unprotect(Convert.ToBase64String(tampered)));
    }
}
