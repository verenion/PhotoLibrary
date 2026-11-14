using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Scanning;

namespace PhotoLibrary.Core.Layout;

/// <summary>What a top-level folder of the library is.</summary>
public enum FolderRole
{
    /// <summary>A folder of dated folders that Keepsake files photos into.</summary>
    Date,
    /// <summary>A folder album. Every folder made outside Keepsake is one, left alone unless its album says otherwise.</summary>
    Album,
    /// <summary>Named like a date but not yet confirmed as one: the user decides. Nothing touches it meanwhile.</summary>
    Undecided,
    /// <summary>_Undated, _Duplicates, _Logs, _Vault, _Albums.</summary>
    Keepsake,
    /// <summary>Names starting with '$' or '.', which sync tools use for their own files.</summary>
    Hidden,
}

/// <summary>
/// A top-level folder with its role and, for date-like ones, which layout its files follow. <see cref="Empty"/>
/// means nothing at all is inside (see <see cref="EmptyFolders"/>), not just no photos.
/// </summary>
public sealed record FolderInfo(string Name, FolderRole Role, int Files, DateLayout? Detected, int Matching, bool Empty = false);

/// <summary>
/// A snapshot of which top-level folders are dates and which are albums, for one operation (an organise plan,
/// an import, a move). The role of a folder is never guessed from its name alone: date folders are the ones
/// Keepsake created or the user confirmed, and every other folder is an album, except that a folder <em>named</em>
/// like a date is <see cref="FolderRole.Undecided"/> until the user says which it is.
/// </summary>
public sealed class FolderMap
{
    private readonly LibraryDb _db;
    private readonly HashSet<string> _dateFolders;
    private readonly Dictionary<string, Album> _albums;        // top-level folder name -> physical album
    private readonly Dictionary<string, Album> _legacyAlbums;  // _Albums\<name> -> physical album

    public string Root { get; }
    public DateLayout Dates { get; }

    public FolderMap(LibraryDb db, string root)
    {
        _db = db;
        Root = Path.GetFullPath(root);
        Dates = DateLayout.Find(db.GetSetting(SettingKey));
        _dateFolders = new HashSet<string>(db.GetDateFolders(), StringComparer.OrdinalIgnoreCase);
        _albums = new(StringComparer.OrdinalIgnoreCase);
        _legacyAlbums = new(StringComparer.OrdinalIgnoreCase);
        foreach (var a in db.GetAlbums().Where(a => a.IsPhysical))
        {
            var name = Album.FolderName(a.Name);
            if (Directory.Exists(Path.Combine(Root, LibraryFolders.LegacyAlbums, name))) _legacyAlbums[name] = a;
            else _albums[name] = a;
        }
    }

    /// <summary>The settings key holding the chosen <see cref="DateLayout.Id"/>.</summary>
    public const string SettingKey = "date_layout";

    public static bool IsKeepsakeOrHidden(string name) =>
        LibraryFolders.Reserved.Contains(name) || name.StartsWith('$') || name.StartsWith('.');

    public FolderRole RoleOf(string top)
    {
        if (LibraryFolders.Reserved.Contains(top)) return FolderRole.Keepsake;
        if (top.StartsWith('$') || top.StartsWith('.')) return FolderRole.Hidden;
        if (_dateFolders.Contains(top)) return FolderRole.Date;
        if (_albums.ContainsKey(top)) return FolderRole.Album;
        return DateLayout.LooksLikeDate(top) ? FolderRole.Undecided : FolderRole.Album;
    }

    public bool IsDateFolder(string top) => _dateFolders.Contains(top);

    public Album? AlbumAt(string top) => _albums.GetValueOrDefault(top);

    /// <summary>True for a file inside a folder album (top-level, or under the old _Albums folder).</summary>
    public bool IsInFolderAlbum(string path) => LibraryFolders.TopOf(Root, path) is { } top
        && (RoleOf(top) == FolderRole.Album || top.Equals(LibraryFolders.LegacyAlbums, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the file is where it should stay; false when Organise should file it.</summary>
    public bool IsSettled(MediaRecord r, bool useMtime)
    {
        var rel = Path.GetRelativePath(Root, r.Path);
        var relDir = Path.GetDirectoryName(rel) ?? "";
        var top = LibraryFolders.TopOf(Root, r.Path);
        if (top is null) return false; // loose in the library root
        switch (RoleOf(top))
        {
            case FolderRole.Date:
                return Dates.Matches(relDir); // a date folder in another layout gets re-filed
            case FolderRole.Album:
                return AlbumAt(top)?.Organise != true;
            case FolderRole.Keepsake when top.Equals(LibraryFolders.Undated, StringComparison.OrdinalIgnoreCase):
                return !(useMtime || r.Taken is not null);
            case FolderRole.Keepsake when top.Equals(LibraryFolders.LegacyAlbums, StringComparison.OrdinalIgnoreCase):
                var name = relDir.Split(Path.DirectorySeparatorChar).ElementAtOrDefault(1);
                return name is null || _legacyAlbums.GetValueOrDefault(name)?.Organise != true;
            default:
                return true; // other Keepsake folders, hidden folders, and undecided ones wait for the user
        }
    }

    /// <summary>
    /// The dated path for a photo, or null when its top-level folder is an album or undecided, so filing it
    /// there would mix it into someone's folder. A new top-level folder is recorded as a date folder here.
    /// </summary>
    public string? DatedPath(DateTime taken, string fileName)
    {
        var top = Dates.TopFor(taken);
        if (!_dateFolders.Contains(top))
        {
            if (_albums.ContainsKey(top) || Directory.Exists(Path.Combine(Root, top))) return null;
            _db.AddDateFolders([top]);
            _dateFolders.Add(top);
        }
        return Path.Combine(Root, Dates.RelativeDir(taken), fileName);
    }

    /// <summary>Where a file lives when it belongs to no folder album: its dated folder, else _Undated.</summary>
    public string HomeFor(DateTime? taken, string fileName) =>
        (taken is { } t ? DatedPath(t, fileName) : null) ?? Path.Combine(Root, LibraryFolders.Undated, fileName);

    /// <summary>
    /// For pruning emptied folders: true for folders that must stay even when empty, namely a folder album's
    /// own folder and undecided top-level folders.
    /// </summary>
    public bool KeepWhenEmpty(string dir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        if (parent is null) return true;
        if (Scanner.PathComparer.Equals(parent, Root))
        {
            var role = RoleOf(Path.GetFileName(dir));
            return role is FolderRole.Album or FolderRole.Undecided or FolderRole.Hidden;
        }
        return Scanner.PathComparer.Equals(parent, Path.Combine(Root, LibraryFolders.LegacyAlbums));
    }

    /// <summary>Every top-level folder on disk with its role, and for date-like ones the layout their files follow.</summary>
    public List<FolderInfo> Survey(IReadOnlyList<MediaRecord> library)
    {
        var byTop = library
            .Select(r => (Top: LibraryFolders.TopOf(Root, r.Path), Dir: Path.GetDirectoryName(Path.GetRelativePath(Root, r.Path)) ?? ""))
            .Where(x => x.Top is not null)
            .GroupBy(x => x.Top!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Dir).ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FolderInfo>();
        if (!Directory.Exists(Root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            var role = RoleOf(name);
            if (role is FolderRole.Keepsake or FolderRole.Hidden) continue;
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden)) continue;
            var dirs = byTop.GetValueOrDefault(name) ?? [];
            var (layout, matching) = DateLayout.LooksLikeDate(name) ? DateLayout.Detect(name, dirs) : (null, 0);
            result.Add(new FolderInfo(name, role, dirs.Count, layout, matching, dirs.Count == 0 && EmptyFolders.IsEmpty(dir)));
        }
        return result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
