using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Albums;
using PhotoLibrary.Core.Db;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.App.ViewModels;

/// <summary>Secure albums (vaults): unlocking, moving photos in, settings and deletion.</summary>
public partial class AlbumsViewModel
{
    /// <summary>The selected album's vault view, when the selected album is secure.</summary>
    [ObservableProperty] public partial VaultViewModel? Vault { get; set; }

    public bool SelectedIsSecure => SelectedAlbum?.IsSecure == true;
    public bool SelectedIsNormal => SelectedAlbum is { IsSecure: false };

    private void UpdateVault()
    {
        OnPropertyChanged(nameof(SelectedIsSecure));
        OnPropertyChanged(nameof(SelectedIsNormal));
        if (Vault is { } old && !ReferenceEquals(old.Album, SelectedAlbum)) { old.Dispose(); Vault = null; }
        if (SelectedAlbum is { IsSecure: true } s && Vault is null) Vault = new VaultViewModel(_main, this, s);
    }

    /// <summary>Asks for the album's password (retrying on a wrong one) unless it is already unlocked.</summary>
    private async Task<bool> EnsureUnlockedAsync(AlbumViewModel vm, string why)
    {
        if (_main.Vaults.IsUnlocked(vm.Id)) return true;
        if (_main.PromptPassword is null) return false;
        var folder = State.Albums.FolderFor(vm.Album);
        var ok = await _main.PromptPassword($"Unlock “{vm.Name}”", why, async pw =>
        {
            try { return await _main.Vaults.TryUnlockAsync(vm.Album, folder, pw) ? null : VaultCrypto.WrongPassword; }
            catch (VaultAuthenticationException ex) { return ex.Message; } // a damaged key file
        });
        if (ok && Vault is { } v && ReferenceEquals(v.Album, vm)) v.LoadItems();
        return ok;
    }

    /// <summary>
    /// Encrypts photos into a vault after confirming: the originals are deleted for good, and every trace leaves
    /// the library (rows, AI vectors and tags, video frames, quality, events, other albums, cached thumbnails).
    /// </summary>
    private async Task AddToVaultAsync(AlbumViewModel vm, IReadOnlyList<MediaRecord> records, bool confirm = true)
    {
        if (!await EnsureUnlockedAsync(vm, $"Enter the password for “{vm.Name}” to move {records.Count:N0} item(s) into it.")) return;
        if (confirm)
        {
            var shas = records.Select(r => r.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ids = records.Select(r => r.Id).ToHashSet();
            var copies = shas.Sum(s => State.Db.GetBySha(s).Count(r => !ids.Contains(r.Id) && r.InLibrary));
            var what = records.Count == 1 ? $"“{records[0].FileName}”" : $"{records.Count:N0} items";
            var message = $"{what} will be encrypted into “{vm.Name}”. The original files are then deleted permanently (not to the Recycle Bin) " +
                          "and removed from the library, its search index, albums and every other view.";
            if (copies > 0) message += $"\n\n{copies:N0} identical cop{(copies == 1 ? "y" : "ies")} elsewhere in the library will stay unencrypted. Use the Duplicates tab to find them.";
            if (_main.Confirm is null || !await _main.Confirm("Move into the vault?", message, "Encrypt and move")) return;
        }
        if (_main.Vaults.Key(vm.Id) is not { } key) return;

        IsBusy = true;
        _main.IsBusy = true;
        var progress = new Progress<(int Done, int Total, string Name)>(p =>
        {
            _main.ProgressPercent = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            _main.StatusText = $"Encrypting {p.Done:N0} / {p.Total:N0}  —  {p.Name}";
        });
        AlbumService.VaultChange change;
        try { change = await Task.Run(() => State.Albums.AddToVault(vm.Album, key, records, progress)); }
        finally { IsBusy = false; _main.IsBusy = false; _main.ProgressPercent = 0; }

        _main.PurgeFromLibrary(change.Vaulted);
        Reload();
        if (Vault is { } v && ReferenceEquals(v.Album, vm)) v.LoadItems();
        _main.StatusText = change.Errors.Count == 0
            ? $"Moved {change.Vaulted.Count:N0} item(s) into “{vm.Name}”. The originals were deleted."
            : $"Moved {change.Vaulted.Count:N0} item(s) into “{vm.Name}”; {change.Errors.Count:N0} were left untouched: {change.Errors[0]}";
    }

    /// <summary>Settings for a secure album, or an album becoming one.</summary>
    private async Task ApplySecureSettingsAsync(AlbumViewModel vm, AlbumSettingsResult settings)
    {
        var album = vm.Album;
        if (!album.IsSecure)
        {
            // Becoming a vault: create it, then encrypt the members in (the dialog already warned about this).
            if (settings.Password is not { } pw) return;
            var members = State.Db.GetAlbumItems(album.Id);
            State.Albums.Rename(album, settings.Name);
            album.Colour = settings.Colour;
            album.VaultId = await Task.Run(() => VaultStore.Create(State.LibraryRoot, pw, State.Albums.Kdf)); // Argon2id: keep it off the UI thread
            album.Kind = AlbumKind.Secure;
            album.CoverFileId = null;
            State.Db.UpdateAlbum(album);
            await _main.Vaults.UnlockAsync(album, State.Albums.FolderFor(album), pw);
            Reload();
            var fresh = Albums.First(a => a.Id == album.Id);
            if (members.Count > 0) await AddToVaultAsync(fresh, members, confirm: false);
            _main.StatusText = $"“{album.Name}” is now a secure album.";
            return;
        }

        State.Albums.Rename(album, settings.Name); // a vault's folder name never changes, so this only renames
        album.Colour = settings.Colour;
        State.Db.UpdateAlbum(album);
        if (settings.Password is { } newPw && settings.CurrentPassword is { } current)
        {
            try
            {
                var changed = await Task.Run(() => VaultStore.TryChangePassword(State.Albums.FolderFor(album), current, newPw, State.Albums.Kdf));
                _main.StatusText = changed
                    ? $"Changed the password of “{album.Name}”."
                    : "The current password was wrong; the password was not changed.";
            }
            catch (VaultAuthenticationException ex) { _main.StatusText = "The password was not changed: " + ex.Message; }
        }
        else _main.StatusText = $"Saved album “{album.Name}”.";
        Reload();
    }

    /// <summary>Deletes a vault: its photos are decrypted back into the library first, then the vault goes.</summary>
    private async Task DeleteVaultAsync(AlbumViewModel vm)
    {
        if (!await EnsureUnlockedAsync(vm, $"Enter the password for “{vm.Name}”. Its photos are decrypted back into the library before the album is deleted.")) return;
        if (_main.Vaults.Key(vm.Id) is not { } key) return;
        IsBusy = true;
        _main.IsBusy = true;
        _main.ProgressIndeterminate = true;
        _main.StatusText = $"Decrypting “{vm.Name}” back into the library…";
        (List<string> Restored, List<string> Errors) result;
        try { result = await Task.Run(() => State.Albums.DeleteVault(vm.Album, key)); }
        finally { IsBusy = false; _main.IsBusy = false; _main.ProgressIndeterminate = false; }

        if (result.Errors.Count == 0)
        {
            _main.Vaults.Lock(vm.Id);
            if (ReferenceEquals(SelectedAlbum, vm)) SelectedAlbum = null;
        }
        Reload();
        _main.StatusText = result.Errors.Count == 0
            ? $"Deleted secure album “{vm.Name}”. {result.Restored.Count:N0} item(s) are back in the library, unencrypted."
            : $"Kept “{vm.Name}”: {result.Errors.Count:N0} item(s) could not be restored ({result.Errors[0]}). {result.Restored.Count:N0} were restored.";
        if (result.Restored.Count > 0) await _main.RescanAsync();
    }
}
