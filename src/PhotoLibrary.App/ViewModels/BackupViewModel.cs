using System.Globalization;
using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrary.App.Services;
using PhotoLibrary.Core.Backup;
using PhotoLibrary.Core.Vault;

namespace PhotoLibrary.App.ViewModels;

/// <summary>
/// Backs the library up to a chosen folder, on demand or on a schedule, either encrypted and versioned
/// (<see cref="EncryptedBackup"/>) or as a plain copy (<see cref="BackupService"/>). Owned by the main window rather
/// than the Settings tab, so scheduled backups run whichever tab is open. Backups only run while Keepsake is open.
/// </summary>
public sealed partial class BackupViewModel : ViewModelBase
{
    public sealed record ScheduleOption(string Key, string Label);
    public sealed record FormatOption(string Key, string Label, string Hint);
    public sealed record KeepOption(int Days, string Label);

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource? _cts;
    private bool _ranThisSession;
    private bool _loading = true;

    private AppSettings Settings => _main.State.Settings;

    public BackupViewModel(MainViewModel main)
    {
        _main = main;
        Folder = Settings.BackupFolder;
        Schedule = ScheduleOptions.FirstOrDefault(o => o.Key == Settings.BackupSchedule) ?? ScheduleOptions[2];
        Format = FormatOptions.FirstOrDefault(o => o.Key == Settings.BackupFormat) ?? FormatOptions[0];
        Keep = KeepOptions.FirstOrDefault(o => o.Days == Settings.BackupKeepDays) ?? KeepOptions[2];
        _timer.Tick += (_, _) => RunIfDue();
        _loading = false;
        RefreshKeyState();
    }

    public ScheduleOption[] ScheduleOptions { get; } =
    [
        new("manual", "Only when I click Back up now"),
        new("startup", "Each time Keepsake opens"),
        new("daily", "Once a day"),
        new("weekly", "Once a week"),
    ];

    public FormatOption[] FormatOptions { get; } =
    [
        new("encrypted", "Encrypted (recommended)",
            "Every file is encrypted and compressed where that helps, with names and folders hidden too, so the backup is safe in OneDrive or on a lost drive. Earlier versions are kept, so deleted or damaged photos can be restored. Restoring needs Keepsake and your backup password or recovery key."),
        new("plain", "Plain copy",
            "An ordinary copy of the library folder that any computer can open. Nothing is encrypted, so anyone with access to the backup can see your photos. Replaced and removed files move to its _Archive folder."),
    ];

    public KeepOption[] KeepOptions { get; } =
    [
        new(30, "30 days"),
        new(90, "90 days"),
        new(365, "1 year"),
        new(0, "Forever"),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFolder), nameof(CanRun), nameof(CanUseEncrypted))]
    public partial string? Folder { get; set; }

    [ObservableProperty] public partial ScheduleOption? Schedule { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEncrypted), nameof(FormatHint), nameof(CanUseEncrypted))]
    public partial FormatOption? Format { get; set; }

    [ObservableProperty] public partial KeepOption? Keep { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun), nameof(CanUseEncrypted))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? ProgressText { get; set; }

    /// <summary>For an encrypted backup: whether this PC holds its key, so backups can run unattended.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsUnlock), nameof(CanUseEncrypted))]
    public partial bool HasKey { get; set; }

    public bool HasFolder => Folder is not null;
    public bool CanRun => HasFolder && !IsRunning;
    public bool IsEncrypted => Format?.Key == "encrypted";
    public string FormatHint => Format?.Hint ?? "";
    public bool NeedsUnlock => IsEncrypted && HasFolder && !HasKey;
    public bool CanUseEncrypted => IsEncrypted && HasFolder && HasKey && !IsRunning;

    public string LastBackupText
    {
        get
        {
            var last = Settings.LastBackupUtc is { } t ? $"Last successful backup: {t.ToLocalTime():d MMM yyyy, HH:mm}." : "No successful backup yet.";
            return Settings.LastBackupSummary is { } s ? $"{last} {s}" : last;
        }
    }

    partial void OnScheduleChanged(ScheduleOption? value)
    {
        if (value is not null && value.Key != Settings.BackupSchedule) Settings.BackupSchedule = value.Key;
    }

    partial void OnKeepChanged(KeepOption? value)
    {
        if (value is not null && value.Days != Settings.BackupKeepDays) Settings.BackupKeepDays = value.Days;
    }

    partial void OnFormatChanged(FormatOption? value)
    {
        if (_loading || value is null || value.Key == Settings.BackupFormat) return;
        Settings.BackupFormat = value.Key;
        // A folder set up for one format can't take the other: choose again.
        if (Folder is { } f && Validate(f) is { } problem)
        {
            Settings.BackupFolder = null;
            Folder = null;
            ProgressText = problem + " Choose a folder for the new format.";
        }
        RefreshKeyState();
    }

    private string? Validate(string folder) => IsEncrypted
        ? EncryptedBackup.Validate(_main.State.LibraryRoot, folder)
        : BackupService.ValidatePlain(_main.State.LibraryRoot, folder);

    /// <summary>Starts the schedule. Called once the window is open and the first library scan has started.</summary>
    public void Start()
    {
        _timer.Start();
        // Give the startup scan a head start before checking.
        DispatcherTimer.RunOnce(RunIfDue, TimeSpan.FromMinutes(1));
    }

    public void Cancel() => _cts?.Cancel();

    // ---- folder and keys

    /// <summary>
    /// Uses <paramref name="folder"/> for backups. For an encrypted backup this either connects to the backup
    /// already there (asking for its password or recovery key) or sets a new one up (asking for a new password
    /// and showing the recovery key).
    /// </summary>
    public async Task SetFolderAsync(string? folder)
    {
        if (folder is null)
        {
            Settings.BackupFolder = null;
            Settings.BackupKey = null;
            Folder = null;
            ProgressText = null;
            RefreshKeyState();
            return;
        }
        if (Validate(folder) is { } problem) { ProgressText = problem; return; }
        if (IsEncrypted)
        {
            var connected = EncryptedBackup.IsEncryptedBackup(folder)
                ? await ConnectAsync(folder)
                : await CreateAsync(folder);
            if (!connected) return;
        }
        Settings.BackupFolder = folder;
        Folder = folder;
        ProgressText = null;
        RefreshKeyState();
    }

    private async Task<bool> CreateAsync(string folder)
    {
        if (_main.PromptNewPassword is null || _main.ShowRecoveryKey is null) return false;
        var password = await _main.PromptNewPassword("Choose a backup password",
            "Your backups will be encrypted with this password. Keepsake remembers it on this PC, protected by your Windows sign-in, so backups run on their own. You'll need it to restore on another PC.");
        if (password is null) return false;
        ProgressText = "Setting up encryption…";
        var kdf = Settings.VaultKdf.Params;
        var (key, recovery) = await Task.Run(() => EncryptedBackup.Create(folder, password, kdf));
        try
        {
            Remember(folder, key);
            ProgressText = null;
            await _main.ShowRecoveryKey(recovery);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<bool> ConnectAsync(string folder)
    {
        if (_main.PromptPassword is null) return false;
        return await _main.PromptPassword("Open this encrypted backup",
            "This folder already holds a Keepsake encrypted backup. Enter its password, or its recovery key, to back up to it from this PC.",
            async secret =>
            {
                try
                {
                    var key = await Task.Run(() => EncryptedBackup.TryUnlock(folder, secret));
                    if (key is null) return "That's neither the password nor the recovery key for this backup.";
                    try { Remember(folder, key); } finally { CryptographicOperations.ZeroMemory(key); }
                    return null;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or VaultAuthenticationException)
                {
                    return "The backup's key file can't be read: " + ex.Message;
                }
            });
    }

    private void Remember(string folder, byte[] key)
    {
        var id = EncryptedBackup.ReadKeyFile(folder).BackupId;
        if (KeyProtector.Protect(key) is { } blob) Settings.BackupKey = (id, blob);
        else _sessionKey = (id, (byte[])key.Clone()); // no DPAPI: keep it for this session only
    }

    private (string Id, byte[] Key)? _sessionKey;

    /// <summary>The backup key for the current folder, from this PC's protected store; null when it isn't here.</summary>
    private byte[]? LoadKey()
    {
        if (Folder is not { } folder || !EncryptedBackup.IsEncryptedBackup(folder)) return null;
        string id;
        try { id = EncryptedBackup.ReadKeyFile(folder).BackupId; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or VaultAuthenticationException) { return null; }
        if (_sessionKey is { } s && s.Id == id) return (byte[])s.Key.Clone();
        return Settings.BackupKey is { } stored && stored.BackupId == id ? KeyProtector.Unprotect(stored.ProtectedKey) : null;
    }

    private void RefreshKeyState()
    {
        if (!IsEncrypted || Folder is null) { HasKey = false; return; }
        var key = LoadKey();
        HasKey = key is not null;
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>Asks for the password (or recovery key) of the current folder's backup and remembers its key.</summary>
    [RelayCommand]
    private async Task Unlock()
    {
        if (Folder is { } f && await ConnectAsync(f)) RefreshKeyState();
    }

    [RelayCommand]
    private async Task ChangePassword()
    {
        if (Folder is not { } folder || _main.PromptPassword is null || _main.PromptNewPassword is null) return;
        string? current = null;
        var ok = await _main.PromptPassword("Change the backup password",
            "Enter the current password, or the recovery key if you've forgotten it.",
            async secret =>
            {
                var key = await Task.Run(() => EncryptedBackup.TryUnlock(folder, secret));
                if (key is null) return "That's neither the password nor the recovery key for this backup.";
                CryptographicOperations.ZeroMemory(key);
                current = secret;
                return null;
            });
        if (!ok || current is null) return;
        var newPassword = await _main.PromptNewPassword("New backup password", "The recovery key stays the same.");
        if (newPassword is null) return;
        var changed = await Task.Run(() => EncryptedBackup.TryChangePassword(folder, current, newPassword));
        ProgressText = changed ? "The backup password was changed." : "The password couldn't be changed.";
    }

    [RelayCommand]
    private Task ClearFolder() => SetFolderAsync(null);

    [RelayCommand]
    private Task BackUpNow() => RunAsync(interactive: true);

    // ---- running

    private void RunIfDue()
    {
        if (IsRunning || Folder is null || _main.IsBusy) return;   // a busy scan/organise: try again next tick
        var last = Settings.LastBackupUtc;
        var due = Settings.BackupSchedule switch
        {
            "startup" => !_ranThisSession,
            "daily" => last is null || DateTime.UtcNow - last > TimeSpan.FromHours(23),
            "weekly" => last is null || DateTime.UtcNow - last > TimeSpan.FromDays(7) - TimeSpan.FromHours(1),
            _ => false,
        };
        if (due) _ = RunAsync(interactive: false);
    }

    private async Task RunAsync(bool interactive)
    {
        if (!CanRun) return;
        var library = _main.State.LibraryRoot;
        var folder = Folder!;
        var db = _main.State.Db;
        byte[]? key = null;
        if (IsEncrypted)
        {
            key = LoadKey();
            if (key is null && interactive && await ConnectAsync(folder)) key = LoadKey();
            RefreshKeyState();
            if (key is null)
            {
                _ranThisSession = true;
                Settings.LastBackupSummary = "The backup didn't run: Keepsake needs the backup password on this PC (Settings › Backup › Enter password).";
                OnPropertyChanged(nameof(LastBackupText));
                return;
            }
        }
        IsRunning = true;
        _ranThisSession = true;
        Progress = 0;
        ProgressText = "Starting backup…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "Saving the database…" : $"{p.Done:N0} / {p.Total:N0}  —  {p.Current}";
        });
        string summary;
        try
        {
            var ct = _cts.Token;
            TimeSpan? keepFor = Settings.BackupKeepDays > 0 ? TimeSpan.FromDays(Settings.BackupKeepDays) : null;
            var r = await Task.Run(() => key is not null
                ? EncryptedBackup.Run(library, folder, key, db.SnapshotTo, keepFor, progress, ct)
                : BackupService.Run(library, folder, db.SnapshotTo, progress, ct), ct);
            summary = Describe(r, key is not null);
            if (r.Ok) Settings.LastBackupUtc = DateTime.UtcNow;
            ProgressText = r.Ok ? null : "Problems: " + string.Join("  •  ", r.Errors.Take(5)) + (r.Errors.Count > 5 ? $"  (+{r.Errors.Count - 5:N0} more)" : "");
        }
        catch (OperationCanceledException)
        {
            summary = "The last backup was cancelled; the next one carries on where it stopped.";
            ProgressText = null;
        }
        catch (Exception ex)
        {
            summary = $"The last backup failed: {ex.Message}";
            ProgressText = null;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
        Settings.LastBackupSummary = summary;
        OnPropertyChanged(nameof(LastBackupText));
        if (!_main.IsBusy) _main.StatusText = "Backup: " + summary;
    }

    /// <summary>Decrypts the newest version of every file and checks it, writing nothing.</summary>
    [RelayCommand]
    private async Task Verify()
    {
        if (!CanUseEncrypted || LoadKey() is not { } key) return;
        var folder = Folder!;
        IsRunning = true;
        Progress = 0;
        ProgressText = "Checking the backup…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = p.Current is null ? "Finishing…" : $"Checking {p.Done:N0} / {p.Total:N0}";
        });
        try
        {
            var ct = _cts.Token;
            var r = await Task.Run(() => EncryptedBackup.Verify(folder, key, null, progress, ct), ct);
            ProgressText = r.Ok
                ? $"The backup is healthy: {r.Checked:N0} files decrypted and checked in {r.Elapsed:h\\:mm\\:ss}."
                : $"{r.Problems.Count:N0} problem{(r.Problems.Count == 1 ? "" : "s")}: " + string.Join("  •  ", r.Problems.Take(5)) + ". The next backup stores damaged files again if they're still in the library.";
        }
        catch (OperationCanceledException) { ProgressText = "Check cancelled."; }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>The backup key for the restore window; the caller zeroes it when done.</summary>
    public byte[]? KeyForRestore() => CanUseEncrypted ? LoadKey() : null;

    private static string Describe(BackupResult r, bool encrypted)
    {
        var parts = new List<string>();
        if (r.Copied > 0) parts.Add($"{r.Copied:N0} new");
        if (r.Updated > 0) parts.Add($"{r.Updated:N0} changed");
        if (r.Archived > 0) parts.Add(encrypted ? $"{r.Archived:N0} no longer in the library (kept in earlier versions)" : $"{r.Archived:N0} removed from the library and archived");
        if (r.Expired > 0) parts.Add($"{r.Expired:N0} old version{(r.Expired == 1 ? "" : "s")} cleaned up");
        var what = parts.Count == 0 ? "nothing new to copy" : string.Join(", ", parts) + $" ({FormatBytes(r.BytesCopied)})";
        var errors = r.Ok ? "" : $", {r.Errors.Count:N0} problem{(r.Errors.Count == 1 ? "" : "s")}";
        return $"{(r.Ok ? "Done" : "Finished with problems")}: {what}{errors}, in {r.Elapsed:h\\:mm\\:ss}.";
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        _ => (b / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
    };
}
