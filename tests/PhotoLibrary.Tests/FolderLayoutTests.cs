using PhotoLibrary.Core;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Layout;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;
using Xunit;

namespace PhotoLibrary.Tests;

/// <summary>
/// Top-level folders are date folders only when Keepsake made them or the user said so; folders made elsewhere
/// are albums; folders merely named like dates wait for the user.
/// </summary>
public class FolderLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photolib-folders-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDb _db;
    private readonly AlbumService _albums;
    private string Lib => Path.Combine(_root, "Library");
    private static readonly DateTime May31 = new(2012, 5, 31, 9, 0, 0);

    public FolderLayoutTests()
    {
        Directory.CreateDirectory(Lib);
        _db = new LibraryDb(Path.Combine(_root, "test.sqlite"));
        _albums = new AlbumService(_db, Lib);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MediaRecord File_(string rel, DateTime? taken)
    {
        var path = Path.Combine(Lib, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        var r = new MediaRecord { Path = path, Sha256 = rel.GetHashCode().ToString("x").PadLeft(64, '0'), Size = 3, MTime = 1, Kind = MediaKind.Image, InLibrary = true, Taken = taken, Added = DateTime.Now };
        _db.Upsert(r);
        return r;
    }

    private OrganisePlan Plan() => Organizer.Plan(new ScanResult { Records = _db.GetLibrary(), OtherFiles = [] }, useMtime: false, _albums.Folders());

    [Theory]
    [InlineData("2012", true)]
    [InlineData("2012-05-31", true)]
    [InlineData("2012_05_31", true)]
    [InlineData("2012-05", true)]
    [InlineData("4023", false)]     // not a plausible year
    [InlineData("1066", false)]
    [InlineData("Camera Roll", false)]
    [InlineData("2012 Holiday", false)]
    [InlineData("2012-13-01", false)] // no 13th month
    public void RecognisesDateNames(string name, bool dateLike) => Assert.Equal(dateLike, DateLayout.LooksLikeDate(name));

    [Fact]
    public void EveryLayoutRoundTrips()
    {
        foreach (var layout in DateLayout.All)
        {
            var dir = layout.RelativeDir(May31);
            Assert.True(layout.TryParse(dir, out var back), $"{layout.Id}: {dir}");
            Assert.Equal(layout.RelativeDir(back), dir);
        }
        var nested = DateLayout.Default;
        Assert.True(nested.Matches(Path.Combine("2012", "2012_05", "2012_05_31")));
        Assert.False(nested.Matches(Path.Combine("2012", "2011_05", "2011_05_31"))); // levels disagree
        Assert.False(nested.Matches(Path.Combine("2012", "2012_05")));
    }

    [Fact]
    public void DetectsTheLayoutAFolderUses()
    {
        var dirs = new[] { Path.Combine("2019", "05", "31"), Path.Combine("2019", "06", "01"), Path.Combine("2019", "misc") };
        var (layout, matching) = DateLayout.Detect("2019", dirs);
        Assert.Equal("y/m/d", layout!.Id);
        Assert.Equal(2, matching);
    }

    [Fact]
    public void FoldersMadeElsewhereBecomeAlbumsAndDateNamesWait()
    {
        File_(@"Camera Roll\IMG_1.jpg", May31);
        File_(@"4023\x.jpg", May31);
        File_(@"2012\2012_05\2012_05_31\a.jpg", May31);
        File_(@"$Temp\sync.jpg", May31);
        Directory.CreateDirectory(Path.Combine(Lib, "Saved Pictures")); // empty: not worth an album yet

        Assert.True(_albums.SyncFolders(_db.GetLibrary()));
        var names = _db.GetAlbums().Select(a => a.Name).OrderBy(n => n).ToList();
        Assert.Equal(["4023", "Camera Roll"], names);
        Assert.All(_db.GetAlbums(), a => { Assert.True(a.IsPhysical); Assert.False(a.Organise); });

        var map = _albums.Folders();
        Assert.Equal(FolderRole.Undecided, map.RoleOf("2012"));
        Assert.Equal(FolderRole.Hidden, map.RoleOf("$Temp"));
        var survey = map.Survey(_db.GetLibrary());
        var y2012 = Assert.Single(survey, f => f.Name == "2012");
        Assert.Equal(DateLayout.Default, y2012.Detected);
        Assert.Equal(1, y2012.Matching);

        // Nothing is filed while 2012 is undecided, and the album and sync folders are left alone.
        Assert.Empty(Plan().Items);
    }

    [Fact]
    public void UndecidedAndAlbumFoldersBlockFilingUntilTheUserDecides()
    {
        File_(@"2012\holiday.jpg", May31);   // a folder named 2012 that isn't laid out by date
        var loose = File_("loose.jpg", May31);

        var plan = Plan();
        Assert.Empty(plan.Items);
        Assert.Equal(1, plan.Blocked["2012"]);

        _albums.SetRole("2012", FolderRole.Album);
        _albums.SyncFolders(_db.GetLibrary());
        Assert.Contains(_db.GetAlbums(), a => a.Name == "2012" && a.IsPhysical);
        Assert.Equal(1, Plan().Blocked["2012"]); // an album named 2012 still can't take dated photos

        _albums.SetRole("2012", FolderRole.Date);
        Assert.DoesNotContain(_db.GetAlbums(), a => a.Name == "2012");
        plan = Plan();
        Assert.Empty(plan.Blocked);
        Assert.Equal(2, plan.Moves); // holiday.jpg isn't in the layout, so it's filed too
        Assert.Contains(plan.Items, i => i.Record.Id == loose.Id && i.Dest == Path.Combine(Lib, "2012", "2012_05", "2012_05_31", "loose.jpg"));
    }

    [Fact]
    public void ChangingTheLayoutRefilesDateFoldersOnly()
    {
        _db.AddDateFolders(["2012"]);
        File_(@"2012\2012_05\2012_05_31\a.jpg", May31);
        File_(@"Trip\b.jpg", May31);
        _albums.SyncFolders(_db.GetLibrary());
        Assert.Empty(Plan().Items);

        _db.SetSetting(FolderMap.SettingKey, "y-m-d");
        var plan = Plan();
        var item = Assert.Single(plan.Items);
        Assert.Equal(Path.Combine(Lib, "2012-05-31", "a.jpg"), item.Dest);
        Assert.Contains("2012-05-31", _db.GetDateFolders()); // the new folder is recorded as a date folder
    }

    [Fact]
    public void AnEmptiedAlbumKeepsItsFolder()
    {
        _db.AddDateFolders(["2012"]);
        var a = File_(@"2012\2012_05\2012_05_31\a.jpg", May31);
        var album = _albums.Create("Party", AlbumKind.Physical);
        _albums.Add(album, [a]);
        _albums.Remove(album, [_db.GetByPath(Path.Combine(Lib, "Party", "a.jpg"))!]);
        Assert.True(Directory.Exists(Path.Combine(Lib, "Party")));
        Assert.True(File.Exists(Path.Combine(Lib, "2012", "2012_05", "2012_05_31", "a.jpg")));
    }

    [Fact]
    public void AlbumsCantTakeADateFolderName()
    {
        _db.AddDateFolders(["2012"]);
        Directory.CreateDirectory(Path.Combine(Lib, "2012"));
        Assert.NotNull(_albums.FolderProblem("2012"));
        Assert.NotNull(_albums.FolderProblem("_Vault"));
        Assert.Null(_albums.FolderProblem("2012 Holiday"));
        Assert.Throws<InvalidOperationException>(() => _albums.Create("2012", AlbumKind.Physical));
    }

    [Fact]
    public void OnlyCompletelyEmptyFoldersAreOfferedForDeletion()
    {
        Directory.CreateDirectory(Path.Combine(Lib, "Empty", "Nested", "Deeper"));
        File.WriteAllText(Path.Combine(Lib, "Empty", "Nested", "desktop.ini"), "");
        Directory.CreateDirectory(Path.Combine(Lib, "2015"));
        Directory.CreateDirectory(Path.Combine(Lib, "Docs"));
        File.WriteAllText(Path.Combine(Lib, "Docs", "notes.txt"), "keep me");
        File_(@"Trip\a.jpg", May31);
        _albums.SetRole("2015", FolderRole.Date);

        var survey = _albums.Folders().Survey(_db.GetLibrary()).ToDictionary(f => f.Name);
        Assert.True(survey["Empty"].Empty);
        Assert.True(survey["2015"].Empty);
        Assert.False(survey["Docs"].Empty);   // no photos, but not empty
        Assert.False(survey["Trip"].Empty);

        Assert.Null(_albums.DeleteEmptyFolder("Empty"));
        Assert.Null(_albums.DeleteEmptyFolder("2015"));
        Assert.False(Directory.Exists(Path.Combine(Lib, "Empty")));
        Assert.DoesNotContain("2015", _db.GetDateFolders());

        // Something arrived after the review was opened: it is left alone.
        Assert.NotNull(_albums.DeleteEmptyFolder("Docs"));
        Assert.True(File.Exists(Path.Combine(Lib, "Docs", "notes.txt")));
        Assert.Throws<ArgumentException>(() => _albums.DeleteEmptyFolder(@"..\x"));
    }

    [Fact]
    public void DeletingAnEmptyAlbumFolderForgetsTheAlbum()
    {
        var album = _albums.Create("Party", AlbumKind.Physical);
        Assert.True(Directory.Exists(_albums.FolderFor(album)));
        Assert.Null(_albums.DeleteEmptyFolder("Party"));
        Assert.DoesNotContain(_db.GetAlbums(), a => a.Name == "Party");
    }
}

/// <summary>Duplicates: a copy in an album is kept when that loses nothing.</summary>
public class KeepChoiceTests
{
    private static MediaRecord R(string path, int w = 100, int h = 100) =>
        new() { Path = path, Sha256 = "x", Size = 1, MTime = 1, Kind = MediaKind.Image, Width = w, Height = h, Added = DateTime.Now };

    [Fact]
    public void IdenticalCopiesPreferTheAlbumOne()
    {
        var dated = R(@"C:\L\2026\2026_04\2026_04_01\a.dng");
        var album = R(@"C:\L\Camera Roll\Camera\a.dng");
        Assert.Equal(1, KeepChoice.Choose([dated, album], identical: true, r => r == album));
        Assert.Equal(0, KeepChoice.Choose([dated, album], identical: true, _ => false));
    }

    [Fact]
    public void LooserMatchesNeverGiveUpQuality()
    {
        var big = R(@"C:\L\2026\a.jpg", 4000, 3000);
        var small = R(@"C:\L\Camera Roll\a.jpg", 2000, 1500);
        Assert.Equal(0, KeepChoice.Choose([big, small], identical: false, r => r == small));
        var same = R(@"C:\L\Camera Roll\b.jpg", 4000, 3000);
        Assert.Equal(1, KeepChoice.Choose([big, same], identical: false, r => r == same));

        var raw = R(@"C:\L\2026\a.dng", 4000, 3000);
        var jpeg = R(@"C:\L\Camera Roll\a.jpg", 4000, 3000);
        Assert.Equal(0, KeepChoice.Choose([raw, jpeg], identical: false, r => r == jpeg)); // the RAW stays
    }
}
