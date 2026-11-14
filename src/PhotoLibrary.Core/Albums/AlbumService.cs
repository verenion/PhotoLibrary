using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Layout;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Pairs;
using PhotoLibrary.Core.Scanning;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.Core.Albums;

/// <summary>What an album operation did to the disk: how many files moved, and which rows now have a new path.</summary>
public sealed record AlbumChange(int Added, int Removed, int Moved, List<(long Id, string NewPath)> Relocated, List<string> Errors)
{
    public static readonly AlbumChange None = new(0, 0, 0, [], []);
}

/// <summary>
/// Album membership plus the file moves a physical album implies. A physical album is a top-level folder of
/// the library (or, for albums made by older versions, <c>_Albums\&lt;name&gt;</c>): adding a photo moves its
/// file there (out of any other physical album), removing it moves the file back into its dated folder. Rows keep their ids throughout, so AI
/// vectors, thumbnails and virtual-album membership survive the move.
/// </summary>
public sealed class AlbumService(LibraryDb db, string libraryRoot)
{
    public string Root { get; } = Path.GetFullPath(libraryRoot);

    /// <summary>Argon2id strength for new vaults (from Settings).</summary>
    public KdfParams Kdf { get; set; } = KdfParams.Default;

    /// <summary>A fresh snapshot of which top-level folders are dates and which are albums.</summary>
    public FolderMap Folders() => new(db, Root);

    public string FolderFor(Album album)
    {
        if (album.IsSecure && album.VaultId is { } v) return VaultStore.FolderFor(Root, v);
        var name = Album.FolderName(album.Name);
        var legacy = Path.Combine(Root, LibraryFolders.LegacyAlbums, name);
        return Directory.Exists(legacy) ? legacy : Path.Combine(Root, name);
    }

    /// <summary>Why a folder album can't be called <paramref name="name"/>, or null when it can.</summary>
    public string? FolderProblem(string name, Album? except = null)
    {
        var folder = Album.FolderName(name);
        if (except is not null && Scanner.PathComparer.Equals(folder, Album.FolderName(except.Name))) return null;
        return Folders().RoleOf(folder) switch
        {
            FolderRole.Date => $"“{folder}” is a date folder in the library. Choose another name, or make it an album in Settings › Library folders.",
            FolderRole.Keepsake => $"“{folder}” is one of Keepsake's own folders. Choose another name.",
            _ => null,
        };
    }

    public bool IsInFolder(Album album, string path) => IsUnder(path, FolderFor(album));

    private static bool IsUnder(string path, string dir)
    {
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Where a file belongs once it leaves every physical album: its dated folder, or _Undated.</summary>
    public string HomeFor(MediaRecord r) => HomeFor(r.Taken, r.FileName);

    public string HomeFor(DateTime? taken, string fileName) => Folders().HomeFor(taken, fileName);

    public Album Create(string name, AlbumKind kind, string? colour = null, string? password = null)
    {
        if (kind == AlbumKind.Secure)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("A secure album needs a password.", nameof(password));
            return db.CreateAlbum(name, kind, colour, VaultStore.Create(Root, password, Kdf));
        }
        if (kind == AlbumKind.Physical && FolderProblem(name) is { } problem) throw new InvalidOperationException(problem);
        var album = db.CreateAlbum(name, kind, colour);
        if (album.IsPhysical) Directory.CreateDirectory(FolderFor(album));
        return album;
    }

    public AlbumChange Add(Album album, IReadOnlyList<MediaRecord> records)
    {
        records = db.WithCompanions(records);
        var existing = db.GetAlbumItemIds(album.Id);
        var fresh = records.Where(r => !existing.Contains(r.Id)).ToList();
        if (!album.IsPhysical)
        {
            db.AddToAlbum(album.Id, fresh.Select(r => r.Id));
            return new AlbumChange(fresh.Count, 0, 0, [], []);
        }

        // One physical album per photo: leaving the others is implied by the move.
        var physical = db.GetAlbums().Where(a => a.IsPhysical && a.Id != album.Id).ToDictionary(a => a.Id);
        var memberships = db.AlbumsOf(records.Select(r => r.Id));
        foreach (var other in physical.Values)
        {
            var leaving = records.Where(r => memberships.TryGetValue(r.Id, out var ids) && ids.Contains(other.Id)).Select(r => r.Id).ToList();
            if (leaving.Count > 0) db.RemoveFromAlbum(other.Id, leaving);
        }

        var folder = FolderFor(album);
        var (moved, relocated, errors) = MoveInto(records.Where(r => !IsInFolder(album, r.Path)).ToList(), folder);
        var failed = new HashSet<string>(errors.Select(e => e.Path), Scanner.PathComparer);
        db.AddToAlbum(album.Id, fresh.Where(r => !failed.Contains(r.Path)).Select(r => r.Id));
        return new AlbumChange(fresh.Count(r => !failed.Contains(r.Path)), 0, moved, relocated, errors.Select(e => e.Message).ToList());
    }

    public AlbumChange Remove(Album album, IReadOnlyList<MediaRecord> records)
    {
        records = db.WithCompanions(records);
        db.RemoveFromAlbum(album.Id, records.Select(r => r.Id));
        if (!album.IsPhysical) return new AlbumChange(0, records.Count, 0, [], []);
        var (moved, relocated, errors) = MoveHome(records.Where(r => IsInFolder(album, r.Path)).ToList());
        return new AlbumChange(0, records.Count, moved, relocated, errors.Select(e => e.Message).ToList());
    }

    /// <summary>Renames the album, and its folder when it is physical.</summary>
    public AlbumChange Rename(Album album, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == album.Name) return AlbumChange.None;
        var relocated = new List<(long, string)>();
        if (album.IsPhysical)
        {
            if (FolderProblem(newName, album) is { } problem) return new AlbumChange(0, 0, 0, [], [problem]);
            var oldDir = FolderFor(album);
            var newDir = Path.Combine(Path.GetDirectoryName(oldDir)!, Album.FolderName(newName));
            if (!Scanner.PathComparer.Equals(oldDir, newDir) && Directory.Exists(oldDir))
            {
                if (Directory.Exists(newDir)) return new AlbumChange(0, 0, 0, [], [$"A folder called “{Path.GetFileName(newDir)}” already exists in the library."]);
                Directory.Move(oldDir, newDir);
                foreach (var r in db.GetUnder(oldDir))
                {
                    var np = Path.Combine(newDir, Path.GetRelativePath(oldDir, r.Path));
                    db.UpdatePath(r.Path, np);
                    relocated.Add((r.Id, np));
                }
            }
        }
        album.Name = newName;
        db.UpdateAlbum(album);
        return new AlbumChange(0, 0, relocated.Count, relocated, []);
    }

    /// <summary>Switches an album between virtual and physical, moving its members in or out of the folder.</summary>
    public AlbumChange SetKind(Album album, AlbumKind kind)
    {
        if (album.Kind == kind) return AlbumChange.None;
        if (album.IsSecure || kind == AlbumKind.Secure)
            throw new InvalidOperationException("Use MakeSecure to turn an album into a vault; a vault is left by deleting it, which restores its photos.");
        if (kind == AlbumKind.Physical && FolderProblem(album.Name) is { } problem) return new AlbumChange(0, 0, 0, [], [problem]);
        var members = db.GetAlbumItems(album.Id);
        var folder = FolderFor(album);
        album.Kind = kind;
        db.UpdateAlbum(album);
        if (kind == AlbumKind.Virtual)
        {
            var (moved, relocated, errors) = MoveHome(members.Where(r => IsUnder(r.Path, folder)).ToList());
            TryDeleteEmpty(FolderFor(album));
            return new AlbumChange(0, 0, moved, relocated, errors.Select(e => e.Message).ToList());
        }
        // Now physical: pull the members in (they leave any other physical album by moving).
        var change = Add(album, members);
        Directory.CreateDirectory(FolderFor(album));
        return change;
    }

    /// <summary>Deletes the album. A physical album's files go back to their dated folders first; nothing is deleted from disk.</summary>
    public AlbumChange Delete(Album album)
    {
        if (album.IsSecure) throw new InvalidOperationException("A secure album is deleted with DeleteVault, which needs its key.");
        var change = AlbumChange.None;
        if (album.IsPhysical)
        {
            var members = db.GetAlbumItems(album.Id).Where(r => IsInFolder(album, r.Path)).ToList();
            var (moved, relocated, errors) = MoveHome(members);
            change = new AlbumChange(0, members.Count, moved, relocated, errors.Select(e => e.Message).ToList());
            TryDeleteEmpty(FolderFor(album));
        }
        db.DeleteAlbum(album.Id);
        return change;
    }

    // ---- secure albums (vaults)

    /// <summary>What moving photos into a vault did. <see cref="Vaulted"/> are gone from the library and its indexes.</summary>
    public sealed record VaultChange(List<MediaRecord> Vaulted, List<string> Errors);

    /// <summary>
    /// Encrypts each photo into the album's vault, proves the encrypted copy decrypts to the same bytes, then
    /// deletes the original for good (not to the Recycle Bin) and erases its rows from the library database.
    /// The caller removes AI rows and cached thumbnails, which live outside Core.
    /// </summary>
    public VaultChange AddToVault(Album album, byte[] key, IReadOnlyList<MediaRecord> records, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        if (!album.IsSecure) throw new InvalidOperationException("Not a secure album.");
        records = db.WithCompanions(records);
        var dir = FolderFor(album);
        var vaulted = new List<MediaRecord>();
        var errors = new List<string>();
        var touched = new HashSet<string>(Scanner.PathComparer);
        for (var i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = records[i];
            progress?.Report((i, records.Count, r.FileName));
            VaultItem? item = null;
            try
            {
                item = VaultStore.Add(dir, key, r.Path, VaultItemInfo.From(r));
                File.Delete(r.Path);
                touched.Add(Path.GetDirectoryName(r.Path)!);
                vaulted.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException)
            {
                // Never leave both copies: if the original couldn't be deleted, drop the encrypted one.
                if (item is not null && File.Exists(r.Path)) try { VaultStore.Delete(item); } catch (IOException) { }
                errors.Add($"{r.FileName}: {ex.Message}");
            }
        }
        if (vaulted.Count > 0) db.ForgetFiles(vaulted);
        Organizer.PruneEmptyDirs(Root, touched, Folders().KeepWhenEmpty);
        progress?.Report((records.Count, records.Count, ""));
        return new VaultChange(vaulted, errors);
    }

    /// <summary>Turns an existing album into a vault: creates it, then encrypts the members in.</summary>
    public VaultChange MakeSecure(Album album, string password, byte[]? keyOut = null, IProgress<(int, int, string)>? progress = null, CancellationToken ct = default)
    {
        if (album.IsSecure) return new VaultChange([], []);
        var members = db.GetAlbumItems(album.Id);
        var oldFolder = album.IsPhysical ? FolderFor(album) : null;
        album.VaultId = VaultStore.Create(Root, password, Kdf);
        album.Kind = AlbumKind.Secure;
        album.CoverFileId = null;
        db.UpdateAlbum(album);
        var key = VaultStore.Unlock(FolderFor(album), password);
        try
        {
            var change = AddToVault(album, key, members, progress, ct);
            if (oldFolder is not null) TryDeleteEmpty(oldFolder);
            if (keyOut is not null) key.CopyTo(keyOut, 0);
            return change;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts vault items back into the library's dated folders and removes them from the vault.</summary>
    public (List<string> Restored, List<string> Errors) RestoreFromVault(byte[] key, IReadOnlyList<VaultItem> items)
    {
        var alloc = new DestAllocator();
        var map = Folders();
        var restored = new List<string>();
        var errors = new List<string>();
        // A shot's files go back together, into the folder its photo's date picks.
        var pairs = FilePairs.Find(items, i => i.Info.OriginalName, i => i.Info.Kind, i => i.Info.DurationMs);
        var destOf = new Dictionary<VaultItem, string>(ReferenceEqualityComparer.Instance);
        foreach (var p in pairs)
        {
            var (primary, companions) = alloc.ClaimGroup(map.HomeFor(p.Primary.Info.Taken, p.Primary.Info.OriginalName),
                p.Companions.Select(c => c.Info.OriginalName).ToList());
            destOf[p.Primary] = primary;
            for (var c = 0; c < companions.Count; c++) destOf[p.Companions[c]] = companions[c];
        }
        foreach (var item in items)
        {
            try
            {
                var dest = destOf.TryGetValue(item, out var d) ? d : alloc.Claim(map.HomeFor(item.Info.Taken, item.Info.OriginalName));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                VaultStore.Export(item, key, dest);
                VaultStore.Delete(item);
                restored.Add(dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException)
            {
                errors.Add($"{item.Info.OriginalName}: {ex.Message}");
            }
        }
        return (restored, errors);
    }

    /// <summary>
    /// Deletes a secure album: every photo is decrypted back into the library first, then the vault folder and its
    /// key file go. If any photo can't be restored the vault is kept, so nothing is ever lost.
    /// </summary>
    public (List<string> Restored, List<string> Errors) DeleteVault(Album album, byte[] key)
    {
        if (!album.IsSecure) throw new InvalidOperationException("Not a secure album.");
        var dir = FolderFor(album);
        var damaged = new List<string>();
        var items = Directory.Exists(dir) ? VaultStore.List(dir, key, damaged) : [];
        var (restored, errors) = RestoreFromVault(key, items);
        errors.AddRange(damaged.Select(d => $"{d}: could not be decrypted"));
        if (errors.Count > 0) return (restored, errors);
        try
        {
            File.Delete(Path.Combine(dir, VaultStore.KeyFileName));
            TryDeleteEmpty(dir);
            TryDeleteEmpty(VaultStore.VaultsDir(Root));
        }
        catch (IOException ex) { errors.Add(ex.Message); }
        db.DeleteAlbum(album.Id);
        return (restored, errors);
    }

    /// <summary>
    /// After a rescan, brings albums in line with the folders on disk:
    /// <list type="bullet">
    /// <item>a top-level folder made outside Keepsake that holds photos becomes a folder album, with Organise off;
    /// a folder named like a date is left undecided for the user instead;</item>
    /// <item>a folder under the old <c>_Albums</c> folder with no album row becomes one too;</item>
    /// <item>a folder album whose folder is gone and that has no photos left is removed, and date folders that
    /// no longer exist are forgotten;</item>
    /// <item>every file in a folder album's folder is a member, and members that left the folder are not.</item>
    /// </list>
    /// Returns true when anything changed.
    /// </summary>
    public bool SyncFolders(IReadOnlyList<MediaRecord> library)
    {
        var changed = false;
        var albums = db.GetAlbums();
        var legacyDir = Path.Combine(Root, LibraryFolders.LegacyAlbums);
        var known = new HashSet<string>(albums.Where(a => a.IsPhysical).Select(a => Album.FolderName(a.Name)), Scanner.PathComparer);
        if (Directory.Exists(legacyDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(legacyDir))
            {
                var name = Path.GetFileName(dir);
                if (!known.Add(name)) continue;
                albums.Add(db.CreateAlbum(name, AlbumKind.Physical));
                changed = true;
            }
        }
        if (Directory.Exists(Root))
        {
            var map = Folders();
            var withMedia = new HashSet<string>(library.Select(r => LibraryFolders.TopOf(Root, r.Path)).OfType<string>(), Scanner.PathComparer);
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                if (map.RoleOf(name) != FolderRole.Album || known.Contains(name) || !withMedia.Contains(name)) continue;
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden)) continue;
                known.Add(name);
                albums.Add(db.CreateAlbum(name, AlbumKind.Physical));
                changed = true;
            }
            var goneDates = db.GetDateFolders().Where(n => !Directory.Exists(Path.Combine(Root, n))).ToList();
            if (goneDates.Count > 0) db.RemoveDateFolders(goneDates);
            foreach (var album in albums.Where(a => a.IsPhysical).ToList())
            {
                if (Directory.Exists(FolderFor(album)) || library.Any(r => IsInFolder(album, r.Path))) continue;
                db.DeleteAlbum(album.Id);
                albums.Remove(album);
                changed = true;
            }
        }
        var vaultsDir = VaultStore.VaultsDir(Root);
        if (Directory.Exists(vaultsDir))
        {
            var knownVaults = new HashSet<string>(albums.Where(a => a.VaultId is not null).Select(a => a.VaultId!), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(vaultsDir))
            {
                var id = Path.GetFileName(dir);
                if (knownVaults.Contains(id) || !File.Exists(Path.Combine(dir, VaultStore.KeyFileName))) continue;
                albums.Add(db.CreateAlbum($"Recovered vault {id[..Math.Min(6, id.Length)]}", AlbumKind.Secure, vaultId: id));
                changed = true;
            }
        }
        foreach (var album in albums.Where(a => a.IsPhysical))
        {
            var inFolder = library.Where(r => IsInFolder(album, r.Path)).Select(r => r.Id).ToHashSet();
            var members = db.GetAlbumItemIds(album.Id);
            var add = inFolder.Where(id => !members.Contains(id)).ToList();
            var byId = library.ToDictionary(r => r.Id);
            var drop = members.Where(id => !inFolder.Contains(id) && byId.ContainsKey(id)).ToList();
            if (add.Count > 0) db.AddToAlbum(album.Id, add);
            if (drop.Count > 0) db.RemoveFromAlbum(album.Id, drop);
            changed |= add.Count > 0 || drop.Count > 0;
        }
        return changed;
    }

    // ---- folder roles

    /// <summary>
    /// The user's decision for a top-level folder. Making it a date folder removes its folder album (the files
    /// stay where they are; Organise re-files any that don't fit the date layout). Making it an album stops
    /// Organise touching it. Nothing on disk moves here.
    /// </summary>
    public void SetRole(string folder, FolderRole role)
    {
        var album = db.GetAlbums().FirstOrDefault(a => a.IsPhysical && Scanner.PathComparer.Equals(Album.FolderName(a.Name), folder)
            && !Directory.Exists(Path.Combine(Root, LibraryFolders.LegacyAlbums, folder)));
        if (role == FolderRole.Date)
        {
            if (album is not null) db.DeleteAlbum(album.Id);
            db.AddDateFolders([folder]);
        }
        else if (role == FolderRole.Album)
        {
            db.RemoveDateFolders([folder]);
            if (album is null) db.CreateAlbum(folder, AlbumKind.Physical);
        }
        else throw new ArgumentOutOfRangeException(nameof(role), "A folder can only be made a date folder or an album.");
    }

    /// <summary>
    /// Deletes an empty top-level folder (nothing in it but housekeeping files and empty sub-folders) and forgets
    /// it as a date folder or folder album. Returns null on success, or why it was left alone.
    /// </summary>
    public string? DeleteEmptyFolder(string folder)
    {
        if (folder.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || folder is "." or "..")
            throw new ArgumentException("Expected a top-level folder name.", nameof(folder));
        if (Folders().RoleOf(folder) is FolderRole.Keepsake or FolderRole.Hidden)
            return $"“{folder}” is one of Keepsake's own folders, so it was left alone.";
        if (EmptyFolders.Delete(Path.Combine(Root, folder)) is { } problem) return problem;
        db.RemoveDateFolders([folder]);
        foreach (var album in db.GetAlbums().Where(a => a.IsPhysical && Scanner.PathComparer.Equals(Album.FolderName(a.Name), folder)
                     && !Directory.Exists(FolderFor(a)) && db.GetAlbumItemIds(a.Id).Count == 0))
            db.DeleteAlbum(album.Id);
        return null;
    }

    // ---- moving

    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveInto(List<MediaRecord> records, string folder) =>
        MoveEach(records, r => Path.Combine(folder, r.FileName));

    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveHome(List<MediaRecord> records)
    {
        var map = Folders();
        var touched = new HashSet<string>(Scanner.PathComparer);
        var result = MoveEach(records, r => { touched.Add(Path.GetDirectoryName(r.Path)!); return map.HomeFor(r.Taken, r.FileName); });
        Organizer.PruneEmptyDirs(Root, touched, map.KeepWhenEmpty);
        return result;
    }

    /// <summary>
    /// Moves each record to where <paramref name="dest"/> wants it (renamed on a clash). Companions found among the
    /// records (or in the database) go along with their photo under the same new name, wherever they are asked for.
    /// </summary>
    private (int Moved, List<(long, string)> Relocated, List<(string Path, string Message)> Errors) MoveEach(List<MediaRecord> records, Func<MediaRecord, string> dest)
    {
        var moved = 0;
        var relocated = new List<(long, string)>();
        var errors = new List<(string, string)>();
        records = db.WithCompanions(records);
        var alloc = new DestAllocator();
        var targets = new Dictionary<MediaRecord, string>(ReferenceEqualityComparer.Instance);
        var pairs = FilePairs.Find(records);
        foreach (var p in pairs)
        {
            var (primary, companions) = alloc.ClaimGroup(dest(p.Primary), p.Companions.Select(c => c.FileName).ToList());
            targets[p.Primary] = primary;
            for (var c = 0; c < p.Companions.Count; c++) targets[p.Companions[c]] = companions[c];
        }
        foreach (var r in records)
        {
            var target = targets.TryGetValue(r, out var t) ? t : alloc.Claim(dest(r));
            if (Scanner.PathComparer.Equals(target, r.Path)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(r.Path, target);
                db.UpdatePath(r.Path, target);
                r.Path = target;
                relocated.Add((r.Id, target));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((r.Path, $"{r.FileName}: {ex.Message}"));
            }
        }
        return (moved, relocated, errors);
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
