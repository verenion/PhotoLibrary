using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// The selected secure album. Locked: grey lock tiles and a password box. Unlocked: its photos, with thumbnails
/// and the preview decrypted into memory only, and actions to export a copy, restore to the library or delete.
/// Everything decrypted is dropped when the vault locks (by hand, after idling, or on leaving the album).
/// </summary>
public partial class VaultViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _main;
    private readonly AlbumsViewModel _albums;
    private CancellationTokenSource? _previewCts;

    public AlbumViewModel Album { get; }
    private VaultSession Vaults => _main.Vaults;
    private string Folder => _main.State.Albums.FolderFor(Album.Album);

    public VaultViewModel(MainViewModel main, AlbumsViewModel albums, AlbumViewModel album)
    {
        _main = main;
        _albums = albums;
        Album = album;
        Vaults.Locked += OnLocked;
        RefreshState();
    }

    public ObservableCollection<int> LockedTiles { get; } = [];
    public ObservableCollection<VaultItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial bool IsLocked { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial VaultItemViewModel? Selected { get; set; }
    [ObservableProperty] public partial Bitmap? PreviewBitmap { get; set; }
    [ObservableProperty] public partial string PreviewDetails { get; set; } = "";
    [ObservableProperty] public partial bool PreviewUnavailable { get; set; }

    public bool HasSelection => Selected is not null;
    public int TileSize => _main.State.Settings.TileSize;
    public string LockedHint => $"This album is encrypted. Enter its password to see its {Album.Count:N0} items. {(_main.Vaults.IdleTimeout is { } t ? $"It locks again after {t.TotalMinutes:0} minute{(t.TotalMinutes == 1 ? "" : "s")} without use." : "It stays unlocked until you lock it or close Keepsake.")}";

    private void RefreshState()
    {
        IsLocked = !Vaults.IsUnlocked(Album.Id);
        if (IsLocked) ShowLocked();
        else LoadItems();
    }

    private void ShowLocked()
    {
        ClearItems();
        LockedTiles.Clear();
        for (var i = 0; i < Math.Min(Album.Count, 300); i++) LockedTiles.Add(i);
        OnPropertyChanged(nameof(LockedHint));
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (Password.Length == 0) return;
        IsBusy = true;
        Message = "Unlocking…";
        try
        {
            if (!await Vaults.TryUnlockAsync(Album.Album, Folder, Password))
            {
                Message = VaultCrypto.WrongPassword;
                return;
            }
            Password = "";
            Message = "";
            IsLocked = false;
            LockedTiles.Clear();
            LoadItems();
        }
        catch (VaultAuthenticationException ex) { Message = ex.Message; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { Message = "The vault could not be opened: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Lock() => Vaults.Lock(Album.Id);

    private void OnLocked(long albumId)
    {
        if (albumId != Album.Id) return;
        IsLocked = true;
        ShowLocked();
        _main.StatusText = $"“{Album.Name}” locked.";
    }

    public void LoadItems()
    {
        ClearItems();
        if (Vaults.Key(Album.Id) is not { } key) { IsLocked = true; ShowLocked(); return; }
        var damaged = new List<string>();
        var items = VaultStore.List(Folder, key, damaged);
        foreach (var item in items)
            Items.Add(new VaultItemViewModel(item, () => Vaults.Key(Album.Id), Select) { TileSize = TileSize });
        Message = damaged.Count == 0 ? (items.Count == 0 ? "This vault is empty. Right-click photos anywhere and choose “Add to album” to move them in." : "")
            : $"{damaged.Count:N0} file(s) could not be decrypted (damaged or from another vault): {string.Join(", ", damaged.Take(3))}";
    }

    private void ClearItems()
    {
        Selected = null;
        foreach (var i in Items) i.ReleaseThumbnail();
        Items.Clear();
        ClearPreview();
    }

    private void Select(VaultItemViewModel item)
    {
        if (Selected is { } old) old.IsSelected = false;
        item.IsSelected = true;
        Selected = item;
        Vaults.Touch(Album.Id);
    }

    partial void OnSelectedChanged(VaultItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        ClearPreview();
        if (value is null) return;
        var i = value.Info;
        PreviewDetails = string.Join("\n", new[]
        {
            i.OriginalName,
            i.Taken is { } t ? t.ToString("ddd d MMM yyyy HH:mm:ss") : "no date",
            string.Join("  ·  ", new[] { i.Width is { } w && i.Height is { } h ? $"{w}×{h}" : "", Formatting.Duration(i.DurationMs), Formatting.Bytes(i.Size) }.Where(s => s.Length > 0)),
            ((i.Make ?? "") + " " + (i.Model ?? "")).Trim(),
            i.Lat is { } la && i.Lon is { } lo ? $"GPS {la:0.00000}, {lo:0.00000}" : "",
        }.Where(s => s.Length > 0));
        _ = LoadPreviewAsync(value);
    }

    private async Task LoadPreviewAsync(VaultItemViewModel item)
    {
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var bmp = await item.DecodeAsync(1600, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(Selected, item)) { bmp?.Dispose(); return; }
            if (bmp is null) PreviewUnavailable = true;
            else PreviewBitmap = bmp;
        }
        catch (OperationCanceledException) { }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        var old = PreviewBitmap;
        PreviewBitmap = null;
        old?.Dispose();
        PreviewUnavailable = false;
        PreviewDetails = "";
    }

    /// <summary>Writes a decrypted copy where the user chose. The vault keeps its copy.</summary>
    public async Task ExportSelectedAsync(string destination)
    {
        if (Selected is not { } item || Vaults.Key(Album.Id) is not { } key) return;
        IsBusy = true;
        try
        {
            if (File.Exists(destination)) File.Delete(destination); // the save dialog already asked about overwriting
            await Task.Run(() => VaultStore.Export(item.Item, key, destination));
            _main.StatusText = $"Exported a decrypted copy of “{item.FileName}” to {destination}. That copy is not protected.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultAuthenticationException)
        {
            _main.StatusText = "Export failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (Selected is not { } item || Vaults.Key(Album.Id) is not { } key) return;
        var ok = _main.Confirm is not null && await _main.Confirm("Move back to the library?",
            $"“{item.FileName}” will be decrypted into its dated folder in the library and removed from this vault. It will no longer be protected.",
            "Move to library");
        if (!ok) return;
        IsBusy = true;
        try
        {
            var (restored, errors) = await Task.Run(() => _main.State.Albums.RestoreFromVault(key, [item.Item]));
            _main.StatusText = errors.Count == 0 ? $"Moved “{item.FileName}” back to the library." : "Could not restore: " + errors[0];
            if (restored.Count > 0)
            {
                LoadItems();
                _albums.Reload();
                await _main.RescanAsync();
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } item) return;
        var ok = _main.Confirm is not null && await _main.Confirm("Delete permanently?",
            $"“{item.FileName}” will be deleted from the vault. This cannot be undone: it does not go to the Recycle Bin.",
            "Delete permanently");
        if (!ok) return;
        try
        {
            VaultStore.Delete(item.Item);
            _main.StatusText = $"Deleted “{item.FileName}” from the vault.";
        }
        catch (IOException ex) { _main.StatusText = "Delete failed: " + ex.Message; }
        LoadItems();
        _albums.Reload();
    }

    public void Dispose()
    {
        Vaults.Locked -= OnLocked;
        ClearItems();
    }
}
