using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.Core.Backup;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// Restore from an encrypted backup: pick a version, a folder to restore into and, optionally, which files.
/// Existing files are never overwritten, so restoring into the library only brings back what's missing.
/// </summary>
public sealed partial class RestoreViewModel : ViewModelBase, IDisposable
{
    private readonly string _backupFolder;
    private readonly byte[] _key;
    private CancellationTokenSource? _cts;

    public sealed record VersionRow(SnapshotInfo Info)
    {
        public string Label => $"{Info.TakenUtc.ToLocalTime():dddd d MMMM yyyy, HH:mm}";
        public string Detail => $"{Info.Files:N0} files  ·  {BackupViewModel.FormatBytes(Info.Bytes)}";
    }

    public RestoreViewModel(string backupFolder, byte[] key, string libraryRoot)
    {
        _backupFolder = backupFolder;
        _key = key;
        LibraryRoot = libraryRoot;
        var damaged = new List<string>();
        foreach (var s in EncryptedBackup.ListSnapshots(backupFolder, key, damaged)) Versions.Add(new VersionRow(s));
        SelectedVersion = Versions.FirstOrDefault();
        Status = Versions.Count == 0 ? "This backup has no versions yet. Run a backup first."
            : damaged.Count > 0 ? $"{damaged.Count:N0} version file(s) couldn't be read and aren't listed."
            : "Pick a version and where to restore it.";
    }

    public string LibraryRoot { get; }
    public ObservableCollection<VersionRow> Versions { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial VersionRow? SelectedVersion { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreCommand)), NotifyPropertyChangedFor(nameof(IntoLibrary))]
    public partial string? Target { get; set; }

    /// <summary>Only paths containing this text (e.g. a folder or file name); empty restores everything.</summary>
    [ObservableProperty] public partial string Only { get; set; } = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public bool IntoLibrary => Target is { } t && string.Equals(Path.GetFullPath(t).TrimEnd('\\'), Path.GetFullPath(LibraryRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private bool CanRestore() => SelectedVersion is not null && Target is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        if (SelectedVersion is not { } version || Target is not { } target) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var only = Only.Trim();
        Func<string, bool>? include = only.Length == 0 ? null : p => p.Contains(only, StringComparison.OrdinalIgnoreCase);
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            Status = p.Current is null ? "Finishing…" : $"Restoring {p.Done:N0} / {p.Total:N0}  —  {p.Current}";
        });
        try
        {
            var ct = _cts.Token;
            var r = await Task.Run(() => EncryptedBackup.Restore(_backupFolder, _key, version.Info.Id, target, include, progress, ct), ct);
            Status = $"Restored {r.Restored:N0} files ({BackupViewModel.FormatBytes(r.Bytes)})"
                     + (r.Skipped > 0 ? $"; {r.Skipped:N0} were already there and left as they were" : "")
                     + (r.Ok ? "." : $". {r.Errors.Count:N0} couldn't be restored: " + string.Join("  •  ", r.Errors.Take(5)));
            Restored = r.Restored > 0;
        }
        catch (OperationCanceledException) { Status = "Restore cancelled. Files already restored are kept."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Core.Vault.VaultAuthenticationException)
        {
            Status = "The restore failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>True once something was restored, so the caller can rescan if it went into the library.</summary>
    public bool Restored { get; private set; }

    public void Cancel() => _cts?.Cancel();

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
