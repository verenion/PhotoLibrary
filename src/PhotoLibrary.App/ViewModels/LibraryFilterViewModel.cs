using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// The filter and sort bar above the library grid and an album's grid (each has its own instance). Every option is a plain string in a combo box, so the view is just
/// combo boxes and a text box; <see cref="Matches"/> turns the choices into a predicate. Options that depend
/// on the library (file types, cameras, albums) are rebuilt after each scan.
/// </summary>
public partial class LibraryFilterViewModel : ViewModelBase
{
    public const string AnyType = "All types", Photos = "Photos", Videos = "Videos", Raw = "RAW files";
    public const string AnyCamera = "Any camera";
    public const string Anywhere = "Anywhere", WithGps = "With location", WithoutGps = "Without location";
    public const string AnySize = "Any size";
    public const string AnyRes = "Any resolution";
    public const string AnyShape = "Any shape", Landscape = "Landscape", Portrait = "Portrait", Square = "Square";
    public const string AnyAlbum = "Any album status", InAlbum = "In an album", NoAlbum = "Not in an album";
    public const string Newest = "Newest first", Oldest = "Oldest first", ByName = "Name (A–Z)", Largest = "Largest files first", MostPixels = "Most pixels first";

    private static readonly (string Label, long Min, long Max)[] Sizes =
    [
        (AnySize, 0, long.MaxValue),
        ("Under 1 MB", 0, 1L << 20),
        ("1 to 5 MB", 1L << 20, 5L << 20),
        ("5 to 20 MB", 5L << 20, 20L << 20),
        ("Over 20 MB", 20L << 20, long.MaxValue),
    ];

    private static readonly (string Label, long Min, long Max)[] Resolutions =
    [
        (AnyRes, 0, long.MaxValue),
        ("Under 2 MP", 0, 2_000_000),
        ("2 to 8 MP", 2_000_000, 8_000_000),
        ("8 to 16 MP", 8_000_000, 16_000_000),
        ("Over 16 MP", 16_000_000, long.MaxValue),
    ];

    /// <summary>Raised whenever a choice changes; the owner reloads the grid.</summary>
    public event Action? Changed;

    public ObservableCollection<string> TypeOptions { get; } = [AnyType, Photos, Videos, Raw];
    public ObservableCollection<string> CameraOptions { get; } = [AnyCamera];
    public string[] LocationOptions { get; } = [Anywhere, WithGps, WithoutGps];
    public string[] SizeOptions { get; } = Sizes.Select(s => s.Label).ToArray();
    public string[] ResolutionOptions { get; } = Resolutions.Select(r => r.Label).ToArray();
    public string[] ShapeOptions { get; } = [AnyShape, Landscape, Portrait, Square];
    public ObservableCollection<string> AlbumOptions { get; } = [AnyAlbum, InAlbum, NoAlbum];
    public string[] SortOptions { get; } = [Newest, Oldest, ByName, Largest, MostPixels];

    /// <summary>How the grid is ordered. Not a filter: it doesn't count towards <see cref="IsActive"/>.</summary>
    [ObservableProperty] public partial string Sort { get; set; } = Newest;

    /// <summary>True when the grid isn't in its default state (a filter is on, or the order isn't newest first).</summary>
    public bool IsCustomised => IsActive || Sort != Newest;

    [ObservableProperty] public partial string Type { get; set; } = AnyType;
    [ObservableProperty] public partial string Camera { get; set; } = AnyCamera;
    [ObservableProperty] public partial string Location { get; set; } = Anywhere;
    [ObservableProperty] public partial string Size { get; set; } = AnySize;
    [ObservableProperty] public partial string Resolution { get; set; } = AnyRes;
    [ObservableProperty] public partial string Shape { get; set; } = AnyShape;
    [ObservableProperty] public partial string AlbumChoice { get; set; } = AnyAlbum;
    [ObservableProperty] public partial string NameContains { get; set; } = "";
    [ObservableProperty] public partial bool IsActive { get; set; }

    /// <summary>Album name → file ids in it, refreshed with the album list.</summary>
    private Dictionary<string, HashSet<long>> _albumMembers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<long> _inAnyAlbum = [];

    partial void OnTypeChanged(string value) => Raise();
    partial void OnCameraChanged(string value) => Raise();
    partial void OnLocationChanged(string value) => Raise();
    partial void OnSizeChanged(string value) => Raise();
    partial void OnResolutionChanged(string value) => Raise();
    partial void OnShapeChanged(string value) => Raise();
    partial void OnAlbumChoiceChanged(string value) => Raise();
    partial void OnNameContainsChanged(string value) => Raise();
    partial void OnSortChanged(string value) => Raise();

    private bool _suspend;

    private void Raise()
    {
        if (_suspend) return;
        IsActive = Type != AnyType || Camera != AnyCamera || Location != Anywhere || Size != AnySize
                   || Resolution != AnyRes || Shape != AnyShape || AlbumChoice != AnyAlbum || NameContains.Trim().Length > 0;
        OnPropertyChanged(nameof(IsCustomised));
        Changed?.Invoke();
    }

    [RelayCommand]
    public void Clear()
    {
        _suspend = true;
        Type = AnyType; Camera = AnyCamera; Location = Anywhere; Size = AnySize;
        Resolution = AnyRes; Shape = AnyShape; AlbumChoice = AnyAlbum; NameContains = ""; Sort = Newest;
        _suspend = false;
        Raise();
    }

    /// <summary>Rebuilds the library-dependent option lists, keeping the current choice when it still exists.</summary>
    public void RefreshOptions(IReadOnlyList<MediaRecord> library, IReadOnlyList<AlbumViewModel> albums, Dictionary<long, List<long>> memberships)
    {
        _suspend = true;
        var exts = library.Select(r => r.Extension.TrimStart('.').ToUpperInvariant()).Where(e => e.Length > 0)
            .GroupBy(e => e).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();
        Replace(TypeOptions, [AnyType, Photos, Videos, Raw, .. exts], () => Type, v => Type = v, AnyType);

        var cameras = library.Select(Formatting.Camera).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        Replace(CameraOptions, [AnyCamera, .. cameras], () => Camera, v => Camera = v, AnyCamera);

        var byAlbumId = albums.ToDictionary(a => a.Id, a => a.Name);
        _albumMembers = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        _inAnyAlbum = [];
        foreach (var (fileId, albumIds) in memberships)
        {
            _inAnyAlbum.Add(fileId);
            foreach (var id in albumIds)
            {
                if (!byAlbumId.TryGetValue(id, out var name)) continue;
                if (!_albumMembers.TryGetValue(name, out var set)) _albumMembers[name] = set = [];
                set.Add(fileId);
            }
        }
        Replace(AlbumOptions, [AnyAlbum, InAlbum, NoAlbum, .. albums.Select(a => a.Name)], () => AlbumChoice, v => AlbumChoice = v, AnyAlbum);
        _suspend = false;
        Raise();
    }

    private static void Replace(ObservableCollection<string> target, List<string> items, Func<string> get, Action<string> set, string fallback)
    {
        var current = get();
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
        set(items.Contains(current) ? current : fallback);
    }

    /// <summary>
    /// Orders records by the chosen sort and splits them into headed groups. Date orders group by
    /// <paramref name="dateGroup"/> (the day, by default); other orders give one group.
    /// </summary>
    public List<(string Title, List<MediaRecord> Items)> Arrange(IEnumerable<MediaRecord> records, Func<MediaRecord, string>? dateGroup = null)
    {
        dateGroup ??= r => r.Taken is { } t ? t.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) : "No date";
        var list = records.ToList();
        IEnumerable<MediaRecord> ordered = Sort switch
        {
            Oldest => list.OrderBy(r => r.Taken is null).ThenBy(r => r.Taken).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase),
            ByName => list.OrderBy(r => r.FileName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase),
            Largest => list.OrderByDescending(r => r.Size),
            MostPixels => list.OrderByDescending(r => (long)(r.Width ?? 0) * (r.Height ?? 0)).ThenByDescending(r => r.Size),
            _ => list.OrderBy(r => r.Taken is null).ThenByDescending(r => r.Taken).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase),
        };
        if (Sort is Newest or Oldest)
            return ordered.GroupBy(dateGroup).Select(g => (g.Key, g.ToList())).ToList();
        var all = ordered.ToList();
        return all.Count == 0 ? [] : [(Sort, all)];
    }

    public bool Matches(MediaRecord r)
    {
        if (Type != AnyType)
        {
            var ok = Type switch
            {
                Photos => r.Kind == MediaKind.Image,
                Videos => r.Kind == MediaKind.Video,
                Raw => MediaKinds.IsRaw(r.Path) || r.PairKind.HasFlag(Core.Pairs.PairKind.Raw),
                _ => string.Equals(r.Extension.TrimStart('.'), Type, StringComparison.OrdinalIgnoreCase),
            };
            if (!ok) return false;
        }
        if (Camera != AnyCamera && !string.Equals(Formatting.Camera(r), Camera, StringComparison.OrdinalIgnoreCase)) return false;
        if (Location == WithGps && r.Lat is null) return false;
        if (Location == WithoutGps && r.Lat is not null) return false;
        if (Size != AnySize)
        {
            var (_, min, max) = Sizes.First(s => s.Label == Size);
            if (r.Size < min || r.Size >= max) return false;
        }
        if (Resolution != AnyRes)
        {
            var (_, min, max) = Resolutions.First(s => s.Label == Resolution);
            var px = (long)(r.Width ?? 0) * (r.Height ?? 0);
            if (px < min || px >= max) return false;
        }
        if (Shape != AnyShape)
        {
            if (r.Width is not { } w || r.Height is not { } h || w == 0 || h == 0) return false;
            // Stored sizes are as encoded; EXIF rotation 5–8 swaps the visible edges.
            var rotated = r.Orientation is >= 5 and <= 8;
            var (vw, vh) = rotated ? (h, w) : (w, h);
            var ok = Shape switch { Landscape => vw > vh, Portrait => vh > vw, _ => vw == vh };
            if (!ok) return false;
        }
        if (AlbumChoice != AnyAlbum)
        {
            var ok = AlbumChoice switch
            {
                InAlbum => _inAnyAlbum.Contains(r.Id),
                NoAlbum => !_inAnyAlbum.Contains(r.Id),
                _ => _albumMembers.TryGetValue(AlbumChoice, out var set) && set.Contains(r.Id),
            };
            if (!ok) return false;
        }
        var needle = NameContains.Trim();
        if (needle.Length > 0 && !r.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
