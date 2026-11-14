using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.ViewModels;

/// <summary>What the album settings dialog hands back.</summary>
/// <summary>What the album settings dialog hands back. <paramref name="Password"/> is a new vault password (creating a
/// secure album, or changing one, with <paramref name="CurrentPassword"/>).</summary>
public sealed record AlbumSettingsResult(string Name, string Colour, AlbumKind Kind, long? CoverFileId, string? Password = null, string? CurrentPassword = null, bool Organise = false);

/// <summary>
/// The Albums tab: the list of albums on the left, the selected album's photos on the right. Adding photos
/// happens from any grid's right-click menu, so the work of applying membership (and the file moves a
/// physical album needs) lives here too.
/// </summary>
public partial class AlbumsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    public AppState State => _main.State;

    public AlbumsViewModel(MainViewModel main)
    {
        _main = main;
        // The album grid has its own filters and order, separate from the library's.
        Filter.Changed += () => { if (Filter.IsCustomised || _filterWasCustomised) LoadItems(); _filterWasCustomised = Filter.IsCustomised; };
        main.PhotosRemoved += OnPhotosRemoved;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedCount))
            {
                OnPropertyChanged(nameof(RemoveMenuText));
                RemoveSelectedFromAlbumCommand.NotifyCanExecuteChanged();
                SetCoverCommand.NotifyCanExecuteChanged();
            }
        };
        Reload();
    }

    public ObservableCollection<AlbumViewModel> Albums { get; } = [];
    public ObservableCollection<DayGroupViewModel> Groups { get; } = [];
    public LibraryFilterViewModel Filter { get; } = new();
    private bool _filterWasCustomised;

    /// <summary>How many items the album had when its grid was last built, before filtering.</summary>
    private int _loadedCount = -1;

    /// <summary>The filter bar shows for an ordinary album with something in it.</summary>
    public bool ShowFilterBar => SelectedAlbum is { IsSecure: false } && (_loadedCount > 0 || Filter.IsActive);

    [ObservableProperty] public partial AlbumViewModel? SelectedAlbum { get; set; }
    [ObservableProperty] public partial string HeaderText { get; set; } = "";
    [ObservableProperty] public partial bool HasAlbums { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";

    public bool HasSelectedAlbum => SelectedAlbum is not null;
    public bool SelectedIsPhysical => SelectedAlbum?.IsPhysical == true;

    /// <summary>Re-read every album from the database, keeping the selection where possible.</summary>
    public void Reload()
    {
        var rows = State.Db.GetAlbums();
        var keep = SelectedAlbum?.Id;
        var byId = Albums.ToDictionary(a => a.Id);
        foreach (var gone in Albums.Where(a => rows.All(r => r.Id != a.Id)).ToList()) { gone.ReleaseCover(); Albums.Remove(gone); }
        foreach (var r in rows.Where(r => r.IsSecure))
            r.Count = Core.Vault.VaultStore.CountItems(State.Albums.FolderFor(r)); // vault members aren't in the database
        for (var i = 0; i < rows.Count; i++)
        {
            if (byId.TryGetValue(rows[i].Id, out var vm))
            {
                vm.Update(rows[i]);
                var at = Albums.IndexOf(vm);
                if (at != i) Albums.Move(at, i);
            }
            else Albums.Insert(i, new AlbumViewModel(rows[i], State));
        }
        HasAlbums = Albums.Count > 0;
        var selected = keep is { } id ? Albums.FirstOrDefault(a => a.Id == id) : null;
        if (!ReferenceEquals(selected, SelectedAlbum)) SelectedAlbum = selected;
        else if (selected is { IsSecure: false } && selected.Count != _loadedCount) LoadItems(); // membership changed
        else if (selected is not null) HeaderText = $"{selected.Name}  ·  {selected.Count:N0} items";
        if (SelectedAlbum is null)
            EmptyText = HasAlbums ? "Select an album." : "No albums yet. Create one here, or right-click photos anywhere and choose “Add to album”.";
        foreach (var a in Albums) _ = a.LoadCoverAsync();
        _main.OnAlbumsChanged();
    }

    partial void OnSelectedAlbumChanged(AlbumViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedAlbum));
        OnPropertyChanged(nameof(SelectedIsPhysical));
        UpdateVault();
        LoadItems();
    }

    private void LoadItems()
    {
        foreach (var g in Groups) foreach (var p in g.Photos) p.ReleaseThumbnail();
        Groups.Clear();
        _loadedCount = -1;
        OnPropertyChanged(nameof(ShowFilterBar));
        if (SelectedAlbum is not { } album)
        {
            HeaderText = "";
            EmptyText = HasAlbums ? "Select an album." : "No albums yet. Create one here, or right-click photos anywhere and choose “Add to album”.";
            return;
        }
        if (album.IsSecure)
        {
            HeaderText = $"{album.Name}  ·  {album.Count:N0} items";
            EmptyText = "";
            return;
        }
        var all = State.Db.GetAlbumItems(album.Id);
        _loadedCount = all.Count;
        OnPropertyChanged(nameof(ShowFilterBar));
        var records = Filter.IsActive ? all.Where(Filter.Matches).ToList() : all;
        HeaderText = Filter.IsActive ? $"{album.Name}  ·  {records.Count:N0} of {all.Count:N0} items" : $"{album.Name}  ·  {all.Count:N0} items";
        EmptyText = all.Count == 0 ? "This album is empty. Right-click photos in the Library (or any search result) and choose “Add to album”."
            : records.Count == 0 ? "Nothing in this album matches the filters." : "";
        foreach (var (title, items) in Filter.Arrange(records))
            Groups.Add(new DayGroupViewModel(title, items.Select(r => new PhotoViewModel(r, _main, SelectInGrid))));
        RemoveSelectedFromAlbumCommand.NotifyCanExecuteChanged();
        SetCoverCommand.NotifyCanExecuteChanged();
    }

    private void SelectInGrid(PhotoViewModel photo, SelectMode mode) =>
        _main.ApplySelection(Groups.SelectMany(g => g.Photos).ToList(), photo, mode);

    // ---- creating and editing

    public async Task<AlbumViewModel> CreateAsync(AlbumSettingsResult settings, IReadOnlyList<MediaRecord>? initial = null)
    {
        var album = await Task.Run(() => State.Albums.Create(settings.Name, settings.Kind, settings.Colour, settings.Password));
        if (album.IsPhysical && settings.Organise) { album.Organise = true; State.Db.UpdateAlbum(album); }
        if (album.IsSecure && settings.Password is { } pw)
            await _main.Vaults.UnlockAsync(album, State.Albums.FolderFor(album), pw); // just set: no need to ask again
        Reload();
        var vm = Albums.First(a => a.Id == album.Id);
        if (initial is { Count: > 0 }) await AddAsync(vm, initial);
        else _main.StatusText = $"Created album “{album.Name}”.";
        return vm;
    }

    public async Task ApplySettingsAsync(AlbumViewModel vm, AlbumSettingsResult settings)
    {
        if (settings.Kind == AlbumKind.Secure || vm.Album.IsSecure) { await ApplySecureSettingsAsync(vm, settings); return; }
        var album = vm.Album;
        var errors = new List<string>();
        var moved = 0;
        var relocated = new List<(long, string)>();
        IsBusy = true;
        _main.IsBusy = true;
        _main.ProgressIndeterminate = true;
        _main.StatusText = settings.Kind != album.Kind ? "Moving files…" : "Saving album…";
        try
        {
            await Task.Run(() =>
            {
                var r = State.Albums.Rename(album, settings.Name);
                errors.AddRange(r.Errors); moved += r.Moved; relocated.AddRange(r.Relocated);
                album.Colour = settings.Colour;
                album.CoverFileId = settings.CoverFileId;
                album.Organise = settings.Kind == AlbumKind.Physical && settings.Organise;
                State.Db.UpdateAlbum(album);
                var k = State.Albums.SetKind(album, settings.Kind);
                errors.AddRange(k.Errors); moved += k.Moved; relocated.AddRange(k.Relocated);
            });
        }
        finally
        {
            IsBusy = false;
            _main.IsBusy = false;
            _main.ProgressIndeterminate = false;
        }
        _main.ApplyRelocations(relocated);
        Reload();
        _main.RefreshFolderState();
        _main.StatusText = Describe($"Saved album “{album.Name}”.", moved, errors);
    }

    public async Task DeleteAsync(AlbumViewModel vm)
    {
        if (vm.Album.IsSecure) { await DeleteVaultAsync(vm); return; }
        var album = vm.Album;
        IsBusy = true;
        _main.IsBusy = true;
        _main.ProgressIndeterminate = true;
        AlbumChange change;
        try { change = await Task.Run(() => State.Albums.Delete(album)); }
        finally { IsBusy = false; _main.IsBusy = false; _main.ProgressIndeterminate = false; }
        _main.ApplyRelocations(change.Relocated);
        if (ReferenceEquals(SelectedAlbum, vm)) SelectedAlbum = null;
        Reload();
        _main.StatusText = Describe($"Deleted album “{album.Name}”. No photos were deleted.", change.Moved, change.Errors);
    }

    /// <summary>Adds photos from any grid. Physical albums move the files, so this runs off the UI thread.</summary>
    public async Task AddAsync(AlbumViewModel vm, IReadOnlyList<MediaRecord> records)
    {
        if (records.Count == 0) return;
        if (vm.Album.IsSecure) { await AddToVaultAsync(vm, records); return; }
        var album = vm.Album;
        AlbumChange change;
        if (album.IsPhysical)
        {
            IsBusy = true;
            _main.IsBusy = true;
            _main.ProgressIndeterminate = true;
            _main.StatusText = $"Moving {records.Count:N0} files into “{album.Name}”…";
            try { change = await Task.Run(() => State.Albums.Add(album, records)); }
            finally { IsBusy = false; _main.IsBusy = false; _main.ProgressIndeterminate = false; }
            _main.ApplyRelocations(change.Relocated);
        }
        else change = State.Albums.Add(album, records);
        Reload();
        var what = change.Added == 1 ? "1 photo" : $"{change.Added:N0} photos";
        _main.StatusText = Describe(change.Added == 0 && change.Errors.Count == 0
            ? $"Already in “{album.Name}”."
            : $"Added {what} to “{album.Name}”.", change.Moved, change.Errors);
    }

    [RelayCommand(CanExecute = nameof(HasSelectionInAlbum))]
    private async Task RemoveSelectedFromAlbumAsync()
    {
        if (SelectedAlbum is not { } vm) return;
        var records = _main.SelectedPhotos.Select(p => p.Record).ToList();
        AlbumChange change;
        if (vm.IsPhysical)
        {
            IsBusy = true;
            _main.IsBusy = true;
            _main.ProgressIndeterminate = true;
            _main.StatusText = "Moving files back to their dated folders…";
            try { change = await Task.Run(() => State.Albums.Remove(vm.Album, records)); }
            finally { IsBusy = false; _main.IsBusy = false; _main.ProgressIndeterminate = false; }
            _main.ApplyRelocations(change.Relocated);
        }
        else change = State.Albums.Remove(vm.Album, records);
        _main.ClearSelectionPublic();
        Reload();
        _main.StatusText = Describe($"Removed {change.Removed:N0} from “{vm.Name}”.", change.Moved, change.Errors);
    }

    [RelayCommand(CanExecute = nameof(HasSelectionInAlbum))]
    private void SetCover()
    {
        if (SelectedAlbum is not { } vm || _main.SelectedPhoto is not { } photo) return;
        vm.Album.CoverFileId = photo.Record.Id;
        State.Db.UpdateAlbum(vm.Album);
        vm.ReloadCover();
        _main.StatusText = $"“{photo.FileName}” is now the cover of “{vm.Name}”.";
    }

    private bool HasSelectionInAlbum() =>
        SelectedAlbum is not null && _main.SelectedPhotos.Count > 0 && Groups.SelectMany(g => g.Photos).Any(p => p.IsSelected);

    public string RemoveMenuText => _main.SelectedCount <= 1 ? $"Remove from “{SelectedAlbum?.Name}”" : $"Remove {_main.SelectedCount:N0} items from “{SelectedAlbum?.Name}”";

    [RelayCommand]
    private void ShowFolder()
    {
        if (SelectedAlbum is { IsPhysical: true } vm)
        {
            var dir = State.Albums.FolderFor(vm.Album);
            Directory.CreateDirectory(dir);
            Formatting.OpenExternal(dir);
        }
    }

    private static string Describe(string head, int moved, List<string> errors)
    {
        if (moved > 0) head += $" Moved {moved:N0} files.";
        if (errors.Count > 0) head += $" {errors.Count:N0} could not be moved: {errors[0]}";
        return head;
    }

    private void OnPhotosRemoved(IReadOnlySet<long> ids)
    {
        var dropped = false;
        foreach (var g in Groups.ToList())
        {
            foreach (var p in g.Photos.Where(p => ids.Contains(p.Record.Id)).ToList()) { p.ReleaseThumbnail(); g.Photos.Remove(p); dropped = true; }
            if (g.Photos.Count == 0) Groups.Remove(g);
        }
        if (dropped) Reload();
    }

    /// <summary>Counts and folders change when the library is rescanned (files may have been moved by hand).</summary>
    public void OnLibraryScanned(IReadOnlyList<MediaRecord> library)
    {
        State.Albums.SyncFolders(library);
        Reload();
    }
}
