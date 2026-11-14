using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Organise;
using PhotoLibrary.Core.Scanning;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Db;

namespace PhotoLibrary.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _previewCts;

    public AppState State { get; }

    public MainViewModel(AppState state)
    {
        State = state;
        LibraryRoot = state.LibraryRoot;
        Ai = new AiPanelViewModel(this);
        Filter = new LibraryFilterViewModel();
        Filter.Changed += () => { if (Filter.IsCustomised || _filterWasCustomised) LoadGroups(SelectedNode); _filterWasCustomised = Filter.IsCustomised; };
        Albums = new AlbumsViewModel(this);
        Duplicates = new DuplicatesViewModel(this);
        Quality = new QualityViewModel(this);
        Vaults.IdleTimeout = state.Settings.VaultAutoLockMinutes is > 0 and var m ? TimeSpan.FromMinutes(m) : null;
        Backup = new BackupViewModel(this);
        Settings = new SettingsViewModel(this);
        OnAlbumsChanged();
    }

    public AiPanelViewModel Ai { get; }
    public AlbumsViewModel Albums { get; }
    public LibraryFilterViewModel Filter { get; }
    public DuplicatesViewModel Duplicates { get; }
    public QualityViewModel Quality { get; }
    public SettingsViewModel Settings { get; }
    public BackupViewModel Backup { get; }

    /// <summary>Tab indices in MainWindow: Library, Albums, AI Tools, Duplicates, Quality, Settings.</summary>
    public const int LibraryTab = 0, AlbumsTab = 1, AiTab = 2, DuplicatesTab = 3, QualityTab = 4, SettingsTab = 5;
    [ObservableProperty] public partial int SelectedTab { get; set; }

    /// <summary>Keys of unlocked vaults (memory only).</summary>
    public VaultSession Vaults { get; } = new();

    /// <summary>
    /// Asks for a password. The validator returns null to accept it or an error to show and ask again; the result
    /// says whether a password was accepted. Wired up by the window.
    /// </summary>
    public Func<string, string, Func<string, Task<string?>>, Task<bool>>? PromptPassword { get; set; }

    /// <summary>Asks for a new password (typed twice): title, message. Null when cancelled. Wired up by the window.</summary>
    public Func<string, string, Task<string?>>? PromptNewPassword { get; set; }

    /// <summary>Shows a backup recovery key once, until the user says it's saved. Wired up by the window.</summary>
    public Func<string, Task>? ShowRecoveryKey { get; set; }

    /// <summary>Asks the user a yes/no question. Wired up by the window; a headless host leaves it null and gets "no".</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Raised after photos leave the library (hidden or deleted) so every grid can drop their tiles.</summary>
    public event Action<IReadOnlySet<long>>? PhotosRemoved;

    /// <summary>Rebuild the current grid, e.g. after the tile size changes.</summary>
    public void ReloadGroups() => LoadGroups(SelectedNode);

    // Design-time only
    public MainViewModel() : this(new AppState()) { }

    [ObservableProperty] public partial string LibraryRoot { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string HeaderText { get; set; } = "";
    [ObservableProperty] public partial string LibrarySummary { get; set; } = "";
    [ObservableProperty] public partial bool NeedsOrganise { get; set; }
    /// <summary>The library has videos but ffmpeg isn't installed, so they can't be previewed.</summary>
    [ObservableProperty] public partial bool NeedsFfmpeg { get; set; }

    public void UpdateNeedsFfmpeg() =>
        NeedsFfmpeg = !Core.Imaging.ImageDecoder.FfmpegAvailable && State.Library.Any(r => r.Kind == MediaKind.Video);

    [RelayCommand]
    private void OpenSettings() => SelectedTab = SettingsTab;
    [ObservableProperty] public partial TreeNodeViewModel? SelectedNode { get; set; }
    /// <summary>The photo shown in the preview pane: the last one clicked. Other selected photos are in <see cref="SelectedPhotos"/>.</summary>
    [ObservableProperty] public partial PhotoViewModel? SelectedPhoto { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }
    [ObservableProperty] public partial Bitmap? PreviewBitmap { get; set; }
    [ObservableProperty] public partial string PreviewTitle { get; set; } = "";
    [ObservableProperty] public partial string PreviewDetails { get; set; } = "";
    [ObservableProperty] public partial bool PreviewVisible { get; set; }
    [ObservableProperty] public partial bool PreviewUnavailable { get; set; }

    public ObservableCollection<TreeNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<DayGroupViewModel> Groups { get; } = [];

    private int _progressGeneration;
    private bool _initialized;
    private bool _filterWasCustomised;

    /// <summary>Progress reports arrive asynchronously; ones from a finished operation must not overwrite the final status.</summary>
    public IProgress<ScanProgress> NewProgress()
    {
        var gen = ++_progressGeneration;
        return new Progress<ScanProgress>(p =>
        {
            if (gen != _progressGeneration || !IsBusy) return;
            ProgressIndeterminate = p.Total == 0;
            ProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            StatusText = p.Total == 0 ? p.Phase : $"{p.Phase}: {p.Done:N0} / {p.Total:N0}" + (p.Current is null ? "" : $"  —  {p.Current}");
        });
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RescanAsync();
    }

    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsBusy) return;
        if (!Directory.Exists(State.LibraryRoot))
        {
            StatusText = $"Library folder not found: {State.LibraryRoot}";
            return;
        }
        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        try
        {
            var result = await State.Scanner.IndexTreeAsync(State.LibraryRoot, LibraryFolders.ExcludedFromIndex, true, NewProgress(), _scanCts.Token);
            State.Library = result.Records;
            Core.Pairs.FilePairs.Refresh(State.Db, result.Records);
            NeedsOrganise = Organizer.NeedsOrganising(result.Records, State.Albums.Folders());
            UpdateNeedsFfmpeg();
            RefreshTree();
            var bytes = result.Records.Sum(r => r.Size);
            LibrarySummary = $"{result.Records.Count:N0} items · {Formatting.Bytes(bytes)}";
            StatusText = NeedsOrganise
                ? "Library scanned. Some files are not yet in dated folders — use Organise."
                : "Library scanned.";
            Ai.Refresh();
            Albums.OnLibraryScanned(result.Records);
            RefreshFolderState();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        finally
        {
            _progressGeneration++;
            IsBusy = false;
            ProgressPercent = 0;
            ProgressIndeterminate = false;
        }
    }

    public void CancelScan() => _scanCts?.Cancel();

    public void RefreshTree()
    {
        var days = State.Db.GetDayCounts();
        var undated = State.Db.CountUndated();
        var previous = SelectedNode;
        Nodes.Clear();
        var total = days.Sum(d => d.Count) + undated;
        var all = new TreeNodeViewModel(NodeKind.All, "All photos", total, DateTime.MinValue, DateTime.MaxValue);
        Nodes.Add(all);
        foreach (var year in days.GroupBy(d => d.Day.Year).OrderByDescending(g => g.Key))
        {
            var y = new TreeNodeViewModel(NodeKind.Year, year.Key.ToString(), year.Sum(d => d.Count),
                new DateTime(year.Key, 1, 1), new DateTime(year.Key + 1, 1, 1));
            foreach (var month in year.GroupBy(d => d.Day.Month).OrderByDescending(g => g.Key))
            {
                var from = new DateTime(year.Key, month.Key, 1);
                var m = new TreeNodeViewModel(NodeKind.Month, from.ToString("MMMM", CultureInfo.CurrentCulture), month.Sum(d => d.Count), from, from.AddMonths(1));
                foreach (var day in month.OrderByDescending(d => d.Day))
                {
                    var dFrom = day.Day.ToDateTime(TimeOnly.MinValue);
                    m.Children.Add(new TreeNodeViewModel(NodeKind.Day, dFrom.ToString("ddd d", CultureInfo.CurrentCulture), day.Count, dFrom, dFrom.AddDays(1)));
                }
                y.Children.Add(m);
            }
            Nodes.Add(y);
        }
        if (undated > 0) Nodes.Add(new TreeNodeViewModel(NodeKind.Undated, "Undated", undated, DateTime.MinValue, DateTime.MinValue));

        // Keep the selection on the same range if it still exists, else land on the newest month.
        var target = FindMatching(Nodes, previous) ?? Nodes.Skip(1).FirstOrDefault()?.Children.FirstOrDefault() ?? Nodes.FirstOrDefault();
        foreach (var n in Nodes)
            if (target is not null && (n.Children.Contains(target) || n.Children.Any(c => c.Children.Contains(target))))
            {
                n.IsExpanded = true;
                foreach (var c in n.Children) if (c.Children.Contains(target)) c.IsExpanded = true;
            }
        SelectedNode = target;
    }

    private static TreeNodeViewModel? FindMatching(IEnumerable<TreeNodeViewModel> nodes, TreeNodeViewModel? target)
    {
        if (target is null) return null;
        foreach (var n in nodes)
        {
            if (n.Kind == target.Kind && n.From == target.From && n.To == target.To) return n;
            if (FindMatching(n.Children, target) is { } found) return found;
        }
        return null;
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) => LoadGroups(value);

    private void LoadGroups(TreeNodeViewModel? node)
    {
        foreach (var g in Groups) foreach (var p in g.Photos) p.ReleaseThumbnail();
        Groups.Clear();
        SelectedPhoto = null;
        if (node is null) { HeaderText = ""; return; }

        List<MediaRecord> all = node.Kind switch
        {
            NodeKind.Undated => State.Db.GetLibraryUndated(),
            NodeKind.All => State.Db.GetLibraryRange(new DateTime(1900, 1, 1), new DateTime(2200, 1, 1)),
            _ => State.Db.GetLibraryRange(node.From, node.To),
        };
        var records = Filter.IsActive ? all.Where(Filter.Matches).ToList() : all;
        HeaderText = node.Kind switch
        {
            NodeKind.Year or NodeKind.All => node.Title,
            NodeKind.Month => node.From.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            NodeKind.Day => node.From.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture),
            _ => "Undated",
        } + (Filter.IsActive ? $"  ·  {records.Count:N0} of {all.Count:N0} items" : $"  ·  {records.Count:N0} items");

        var grouped = Filter.Arrange(records, node.Kind == NodeKind.Undated
            ? r => Path.GetRelativePath(State.LibraryRoot, Path.GetDirectoryName(r.Path) ?? "")
            : null);
        foreach (var (title, items) in grouped)
            Groups.Add(new DayGroupViewModel(title, items.Select(r => new PhotoViewModel(r, this, SelectInGrid))));
    }

    // ---- album badges

    private Dictionary<long, List<AlbumViewModel>> _albumsByFile = [];

    /// <summary>The albums a photo is in, for the dots on its tile.</summary>
    public IReadOnlyList<AlbumViewModel> AlbumsFor(long fileId) =>
        _albumsByFile.TryGetValue(fileId, out var list) ? list : [];

    /// <summary>Called by the Albums tab after any membership change: refresh badges, filter options and the grid.</summary>
    public void OnAlbumsChanged()
    {
        // The Albums view model loads its list in its own constructor, before this property is assigned;
        // the constructor below calls this again once everything exists.
        if (Albums is null) return;
        var memberships = State.Db.AllMemberships();
        var byId = Albums.Albums.ToDictionary(a => a.Id);
        _albumsByFile = memberships.ToDictionary(kv => kv.Key, kv => kv.Value.Where(byId.ContainsKey).Select(id => byId[id]).ToList());
        foreach (var p in Groups.SelectMany(g => g.Photos)) p.RefreshAlbums();
        foreach (var p in Albums.Groups.SelectMany(g => g.Photos)) p.RefreshAlbums();
        foreach (var p in Ai.Results) p.RefreshAlbums();
        Filter.RefreshOptions(State.Library, Albums.Albums.Where(a => !a.IsSecure).ToList(), memberships); // raises Changed, so an active filter re-applies
        Albums.Filter.RefreshOptions(State.Library, Albums.Albums.Where(a => !a.IsSecure).ToList(), memberships);
    }

    // ---- selection (shared by every grid: library, AI results, duplicates, quality)

    private readonly List<PhotoViewModel> _selected = [];
    private PhotoViewModel? _anchor;

    public IReadOnlyList<PhotoViewModel> SelectedPhotos => _selected;

    private void SelectInGrid(PhotoViewModel photo, SelectMode mode) =>
        ApplySelection(Groups.SelectMany(g => g.Photos).ToList(), photo, mode);

    /// <summary>
    /// Click handling for any grid. <paramref name="ordered"/> is that grid's photos in display order, used for
    /// shift-click ranges. Selecting in one grid clears a selection made in another.
    /// </summary>
    public void ApplySelection(IReadOnlyList<PhotoViewModel> ordered, PhotoViewModel photo, SelectMode mode)
    {
        if (mode == SelectMode.Range && _anchor is { } anchor && ordered.Contains(anchor) && ordered.Contains(photo))
        {
            var list = ordered as IList<PhotoViewModel> ?? ordered.ToList();
            var a = list.IndexOf(anchor);
            var b = list.IndexOf(photo);
            ClearSelection(keepPreview: true);
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) { ordered[i].IsSelected = true; _selected.Add(ordered[i]); }
            _anchor = anchor;
        }
        else if (mode == SelectMode.Toggle)
        {
            if (photo.IsSelected) { photo.IsSelected = false; _selected.Remove(photo); }
            else { photo.IsSelected = true; _selected.Add(photo); }
            _anchor = photo;
        }
        else
        {
            ClearSelection(keepPreview: true);
            photo.IsSelected = true;
            _selected.Add(photo);
            _anchor = photo;
        }
        SelectedCount = _selected.Count;
        SelectedPhoto = photo.IsSelected ? photo : _selected.LastOrDefault();
        NotifySelectionCommands();
    }

    private void ClearSelection(bool keepPreview)
    {
        foreach (var p in _selected) p.IsSelected = false;
        _selected.Clear();
        _anchor = null;
        SelectedCount = 0;
        if (!keepPreview) SelectedPhoto = null;
    }

    public void ClearSelectionPublic()
    {
        ClearSelection(keepPreview: false);
        NotifySelectionCommands();
    }

    /// <summary>Whether a right-click should offer album actions for the grid it came from.</summary>
    public bool InAlbumGrid => SelectedTab == AlbumsTab && Albums.SelectedAlbum is not null;

    /// <summary>Puts the whole selection into an album.</summary>
    public Task AddSelectedToAlbumAsync(AlbumViewModel album) => Albums.AddAsync(album, _selected.Select(p => p.Record).ToList());

    public IReadOnlyList<MediaRecord> SelectedRecords => _selected.Select(p => p.Record).ToList();

    /// <summary>
    /// Files moved by a physical album keep their row ids but change path; patch every cached record so the
    /// preview pane, "show in folder" and the Organise check see the new location without a rescan.
    /// </summary>
    public void ApplyRelocations(IReadOnlyList<(long Id, string NewPath)> relocated)
    {
        if (relocated.Count == 0) return;
        var map = relocated.ToDictionary(r => r.Id, r => r.NewPath);
        foreach (var r in State.Library) if (map.TryGetValue(r.Id, out var np)) r.Path = np;
        foreach (var p in Groups.SelectMany(g => g.Photos)) if (map.TryGetValue(p.Record.Id, out var np)) p.Record.Path = np;
        foreach (var p in Albums.Groups.SelectMany(g => g.Photos)) if (map.TryGetValue(p.Record.Id, out var np)) p.Record.Path = np;
        if (SelectedPhoto is { } sp && map.ContainsKey(sp.Record.Id))
        {
            var keep = sp;
            SelectedPhoto = null;
            SelectedPhoto = keep;
        }
        NeedsOrganise = Organizer.NeedsOrganising(State.Library, State.Albums.Folders());
    }

    /// <summary>Top-level folders named like dates that the user hasn't yet called dates or albums.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUndecidedFolders), nameof(UndecidedFoldersText))]
    public partial int UndecidedFolders { get; set; }

    public bool HasUndecidedFolders => UndecidedFolders > 0;

    public string UndecidedFoldersText => UndecidedFolders == 1
        ? "1 folder looks like a date: is it a date folder or an album?"
        : $"{UndecidedFolders:N0} folders look like dates: are they date folders or albums?";

    /// <summary>Re-reads folder roles (after a scan, or a change in Settings) and the Organise hint.</summary>
    public void RefreshFolderState()
    {
        var map = State.Albums.Folders();
        UndecidedFolders = map.Survey(State.Library).Count(f => f.Role == PhotoLibrary.Core.Layout.FolderRole.Undecided);
        NeedsOrganise = Organizer.NeedsOrganising(State.Library, map);
    }

    private void NotifySelectionCommands()
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        FindSimilarSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemoveMenuText));
        OnPropertyChanged(nameof(DeleteMenuText));
        OnPropertyChanged(nameof(SelectionText));
    }

    public string SelectionText => SelectedCount <= 1 ? "" : $"{SelectedCount:N0} selected";
    public string RemoveMenuText => SelectedCount <= 1 ? "Remove from library" : $"Remove {SelectedCount:N0} items from library";
    public string DeleteMenuText => SelectedCount <= 1 ? "Delete from disk…" : $"Delete {SelectedCount:N0} items from disk…";

    private bool HasSelection() => _selected.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RemoveSelectedAsync() => RemoveAsync(_selected.Select(p => p.Record).ToList(), fromDisk: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteSelectedAsync() => RemoveAsync(_selected.Select(p => p.Record).ToList(), fromDisk: true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task FindSimilarSelectedAsync()
    {
        SelectedTab = AiTab;
        if (Ai.FindSimilarCommand.CanExecute(null)) await Ai.FindSimilarCommand.ExecuteAsync(null);
    }

    // ---- removing photos

    /// <summary>
    /// Takes photos out of the library. Soft removal hides the rows (the files stay, and are never re-imported);
    /// disk removal asks first, then sends the files to the Recycle Bin and drops the rows. Returns how many went.
    /// A photo's paired files (its RAW, Live Photo clip…) go with it unless <paramref name="withCompanions"/> is false.
    /// </summary>
    public async Task<int> RemoveAsync(IReadOnlyList<MediaRecord> records, bool fromDisk, bool withCompanions = true)
    {
        if (records.Count == 0) return 0;
        if (withCompanions) records = State.Db.WithCompanions(records);
        var n = records.Count;
        var bytes = Formatting.Bytes(records.Sum(r => r.Size));
        var removed = new HashSet<long>();
        if (fromDisk)
        {
            var what = n == 1 ? $"“{records[0].FileName}”" : $"{n:N0} files ({bytes})";
            var ok = Confirm is not null && await Confirm("Delete from disk?",
                $"{what} will be moved to the Recycle Bin and removed from the library. You can restore from the Recycle Bin, but the app will not know about the files until the next rescan.",
                n == 1 ? "Delete" : $"Delete {n:N0} files");
            if (!ok) return 0;
            var failed = await Task.Run(() => FileOps.Recycle(records.Select(r => r.Path).ToList()));
            var failedSet = new HashSet<string>(failed, Scanner.PathComparer);
            var gone = records.Where(r => !failedSet.Contains(r.Path)).ToList();
            State.Db.DeleteByIds(gone.Select(r => r.Id));
            foreach (var r in gone)
            {
                removed.Add(r.Id);
                if (State.Db.GetBySha(r.Sha256).Count == 0) State.Thumbs.Forget(r.Sha256);
            }
            StatusText = failed.Count == 0
                ? $"Moved {gone.Count:N0} files ({bytes}) to the Recycle Bin."
                : $"Moved {gone.Count:N0} files to the Recycle Bin; {failed.Count:N0} could not be deleted.";
        }
        else
        {
            State.Db.Hide(records.Select(r => r.Id));
            foreach (var r in records) removed.Add(r.Id);
            StatusText = n == 1
                ? $"Removed “{records[0].FileName}” from the library. The file is still on disk; restore from Settings."
                : $"Removed {n:N0} items ({bytes}) from the library. The files are still on disk; restore from Settings.";
        }
        if (removed.Count == 0) return 0;
        AfterRemoval(removed);
        return removed.Count;
    }

    /// <summary>
    /// Photos that went into a vault: erase what the library still knows about them (AI vectors, tags, video
    /// frames, quality, events, cached thumbnails), then drop them from every view. The rows themselves were
    /// already deleted by the album service.
    /// </summary>
    public void PurgeFromLibrary(IReadOnlyList<MediaRecord> records)
    {
        if (records.Count == 0) return;
        var ids = records.Select(r => r.Id).ToHashSet();
        State.Db.ForgetAnalysis(ids);
        State.Db.PruneOrphans();
        foreach (var r in records)
            if (State.Db.GetBySha(r.Sha256).Count == 0) State.Thumbs.Forget(r.Sha256);
        AfterRemoval(ids);
    }

    private void AfterRemoval(HashSet<long> removed)
    {
        State.Ai.Forget(removed);
        State.Library.RemoveAll(r => removed.Contains(r.Id));
        Core.Pairs.FilePairs.Refresh(State.Db, State.Library); // a RAW left behind by a removed copy shows on its own
        LibrarySummary = $"{State.Library.Count:N0} items · {Formatting.Bytes(State.Library.Sum(r => r.Size))}";
        foreach (var p in _selected.Where(p => removed.Contains(p.Record.Id)).ToList()) { p.IsSelected = false; _selected.Remove(p); }
        if (SelectedPhoto is { } sp && removed.Contains(sp.Record.Id)) SelectedPhoto = _selected.LastOrDefault();
        SelectedCount = _selected.Count;
        NotifySelectionCommands();
        DropFromGrid(removed);
        PhotosRemoved?.Invoke(removed);
        Settings.RefreshLibrary();
    }

    /// <summary>Take removed photos out of the library grid in place, so the scroll position survives.</summary>
    private void DropFromGrid(IReadOnlySet<long> ids)
    {
        var dropped = 0;
        foreach (var g in Groups.ToList())
        {
            foreach (var p in g.Photos.Where(p => ids.Contains(p.Record.Id)).ToList())
            {
                p.ReleaseThumbnail();
                g.Photos.Remove(p);
                dropped++;
            }
            if (g.Photos.Count == 0) Groups.Remove(g);
        }
        if (dropped > 0 && HeaderText.LastIndexOf("  ·  ", StringComparison.Ordinal) is var cut && cut >= 0)
            HeaderText = HeaderText[..cut] + $"  ·  {Groups.Sum(g => g.Photos.Count):N0} items";
        _treeStale = true;
    }

    private bool _treeStale;

    /// <summary>The date tree's counts are refreshed lazily: rebuilding it reselects a node and reloads the grid.</summary>
    public void RefreshTreeIfStale()
    {
        if (!_treeStale) return;
        _treeStale = false;
        RefreshTree();
    }

    partial void OnSelectedPhotoChanged(PhotoViewModel? value)
    {
        _previewCts?.Cancel();
        var old = PreviewBitmap;
        PreviewBitmap = null;
        old?.Dispose();
        PreviewUnavailable = false;
        if (value is null) { PreviewVisible = false; return; }
        PreviewVisible = true;
        var r = value.Record;
        PreviewTitle = r.FileName;
        var lines = new List<string>
        {
            Formatting.Taken(r),
            Formatting.Camera(r),
            Formatting.Summary(r),
            Path.GetRelativePath(State.LibraryRoot, r.Path),
        };
        if (r.Lat is { } lat && r.Lon is { } lon) lines.Add($"GPS {lat:0.00000}, {lon:0.00000}");
        if (r.Description is { } d) lines.Add("“" + d + "”");
        if (r.SidecarPath is not null) lines.Add("Google Takeout metadata attached");
        if (value.AtMs is { } at) lines.Add($"Best match at {Formatting.Duration(at)} (shown above)");
        PreviewDetails = string.Join("\n", lines.Where(l => l.Length > 0));
        _ = LoadPreviewAsync(value);
    }

    private async Task LoadPreviewAsync(PhotoViewModel photo)
    {
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var bmp = await BitmapLoader.PreviewAsync(photo.Record, 1600, cts.Token, photo.AtMs);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedPhoto, photo)) { bmp?.Dispose(); return; }
            if (bmp is null) PreviewUnavailable = true;
            else PreviewBitmap = bmp;
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public void OpenSelected()
    {
        if (SelectedPhoto is { } p) Formatting.OpenExternal(p.Record.Path);
    }

    [RelayCommand]
    public void ShowSelectedInFolder()
    {
        if (SelectedPhoto is { } p) Formatting.ShowInFolder(p.Record.Path);
    }

    [RelayCommand]
    public void ClosePreview()
    {
        ClearSelection(keepPreview: false);
        NotifySelectionCommands();
    }

    public async Task ChangeLibraryAsync(string newRoot)
    {
        State.SetLibraryRoot(newRoot);
        LibraryRoot = newRoot;
        await RescanAsync();
    }
}
