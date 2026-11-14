using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.Core.Layout;

namespace PhotoLibrary.App.ViewModels;

/// <summary>One top-level folder in the review: what it is now, and what the user wants it to be.</summary>
public sealed partial class FolderRowViewModel : ViewModelBase
{
    private readonly FolderReviewViewModel _owner;

    public FolderRowViewModel(FolderReviewViewModel owner, FolderInfo info, DateLayout current)
    {
        _owner = owner;
        Info = info;
        Current = current;
        // Undecided: suggest "date" only when the files inside really follow a date layout (or there are none).
        var looksDated = info.Files == 0 || info.Matching >= info.Files * 0.8;
        Choice = info.Empty ? DeleteChoice : info.Role switch
        {
            FolderRole.Date => 0,
            FolderRole.Undecided => looksDated ? 0 : 1,
            _ => 1,
        };
    }

    public FolderInfo Info { get; }
    private DateLayout Current { get; }

    public string Name => Info.Name;

    /// <summary>Only a folder with nothing at all in it can be deleted from here.</summary>
    public bool CanDelete => Info.Empty;
    public bool IsUndecided => Info.Role == FolderRole.Undecided;

    public string Detail
    {
        get
        {
            var files = Info.Files == 1 ? "1 photo or video" : $"{Info.Files:N0} photos and videos";
            var now = Info.Role switch
            {
                FolderRole.Date => "Date folder",
                FolderRole.Album => "Folder album",
                _ => "Named like a date: waiting for you",
            };
            if (Info.Empty) return $"{now} · completely empty";
            if (Info.Files == 0) return $"{now} · no photos or videos, but other files inside";
            var layout = Info.Detected is { } l && Info.Matching > 0
                ? $" · {Info.Matching:N0} of them in a {l.Example}-style layout"
                : Info.Detected is not null ? " · not in any date layout" : "";
            return $"{now} · {files}{layout}";
        }
    }

    public const int DateChoice = 0, AlbumChoice = 1, DeleteChoice = 2;

    /// <summary>0 = date folder, 1 = album, 2 = delete (empty folders only).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Warning), nameof(HasWarning))]
    public partial int Choice { get; set; }

    public bool AsDate => Choice == DateChoice;
    public bool ToDelete => Choice == DeleteChoice && CanDelete;

    partial void OnChoiceChanged(int value) => _owner?.ChoicesChanged();

    public string? Warning =>
        ToDelete ? $"“{Name}” and any empty folders inside it will be deleted when you save."
        : Choice == 1 && Current.MatchesTop(Name)
            ? $"While “{Name}” is an album, photos whose date folder would be “{Name}” are left where they are instead of being filed into it."
            : Choice == 0 && Info.Files > 0 && Info.Detected is not null && Info.Matching < Info.Files
                ? $"Organise will re-file {Info.Files - Info.Matching:N0} files in here that aren't in the date layout."
                : Choice == 0 && Info.Detected is null && Info.Files > 0
                    ? "Organise will re-file every file in here into the date layout."
                    : null;

    public bool HasWarning => Warning is not null;
}

/// <summary>
/// The folder review: shown when folders named like dates turn up, and from Settings to change any folder.
/// Nothing on disk changes here; only Keepsake's record of which folders are dates and which are albums.
/// </summary>
public sealed partial class FolderReviewViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DateLayout _current;
    private bool _loading = true;

    public FolderReviewViewModel(MainViewModel main, bool onlyUndecided)
    {
        _main = main;
        var map = main.State.Albums.Folders();
        _current = map.Dates;
        foreach (var f in map.Survey(main.State.Library).Where(f => !onlyUndecided || f.Role == FolderRole.Undecided))
            Rows.Add(new FolderRowViewModel(this, f, _current));
        Heading = onlyUndecided ? "Some folders look like dates" : "Library folders";
        Intro = onlyUndecided
            ? "These top-level folders are named like dates, but Keepsake didn't create them, so it won't guess. Choose for each one: a date folder (Keepsake files photos into it by date) or a folder album (left exactly as it is). Nothing moves until you run Organise, and you can change this later in Settings."
            : "Date folders hold photos filed by date; Keepsake files new photos into them. Folder albums are left exactly as they are. Changing this doesn't move anything until you run Organise.";
        _loading = false;
        ChoicesChanged();
    }

    public ObservableCollection<FolderRowViewModel> Rows { get; } = [];
    public string Heading { get; }
    public string Intro { get; }
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The layout most folders chosen as dates already use, when it isn't the one in Settings.</summary>
    [ObservableProperty] public partial DateLayout? SuggestedLayout { get; set; }
    [ObservableProperty] public partial bool UseSuggestedLayout { get; set; } = true;
    public string SuggestedLayoutText => SuggestedLayout is { } l
        ? $"Use this layout for date folders: {l.Example} ({l.Grain}). Most of the folders you chose as dates already use it; Settings currently says {_current.Example}, so Organise would re-file them all."
        : "";

    partial void OnSuggestedLayoutChanged(DateLayout? value) => OnPropertyChanged(nameof(SuggestedLayoutText));

    public void SetAll(int choice)
    {
        _loading = true;
        // Empty folders keep their own choice (delete, by default); the bulk buttons are about folders with photos.
        foreach (var r in Rows.Where(r => !r.CanDelete)) r.Choice = choice;
        _loading = false;
        ChoicesChanged();
    }

    public void ChoicesChanged()
    {
        if (_loading) return;
        var best = Rows.Where(r => r.AsDate && r.Info.Detected is not null && r.Info.Matching > 0)
            .GroupBy(r => r.Info.Detected!)
            .Select(g => (Layout: g.Key, Files: g.Sum(r => r.Info.Matching)))
            .OrderByDescending(x => x.Files)
            .FirstOrDefault();
        SuggestedLayout = best.Layout is { } l && l != _current ? l : null;
    }

    /// <summary>Records the choices, then rescans so albums and the Organise hint catch up.</summary>
    public async Task SaveAsync()
    {
        var albums = _main.State.Albums;
        var changed = 0;
        var deleted = 0;
        var problems = new List<string>();
        foreach (var r in Rows.Where(r => r.ToDelete))
        {
            if (albums.DeleteEmptyFolder(r.Name) is { } problem) problems.Add(problem);
            else deleted++;
        }
        foreach (var r in Rows.Where(r => !r.ToDelete))
        {
            var want = r.AsDate ? FolderRole.Date : FolderRole.Album;
            if (r.Info.Role == want) continue;
            albums.SetRole(r.Name, want);
            changed++;
        }
        if (SuggestedLayout is { } l && UseSuggestedLayout) _main.State.Settings.DateLayout = l;
        _main.RefreshFolderState();
        var parts = new List<string>();
        if (changed > 0) parts.Add($"Updated {changed:N0} folder{(changed == 1 ? "" : "s")}.");
        if (deleted > 0) parts.Add($"Deleted {deleted:N0} empty folder{(deleted == 1 ? "" : "s")}.");
        parts.AddRange(problems);
        _main.StatusText = parts.Count == 0 ? "No folder changes." : string.Join(" ", parts);
        await _main.RescanAsync();
    }
}
