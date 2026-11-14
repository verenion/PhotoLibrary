using System.Globalization;
using PhotoLibrary.AI;
using PhotoLibrary.AI.Clip;
using PhotoLibrary.Core.Db;

namespace PhotoLibrary.App.Services;

/// <summary>
/// User-tunable settings, persisted in the library database's settings table and pushed into the services
/// that use them. Every setter saves immediately; there is no separate Apply step.
/// </summary>
public sealed class AppSettings
{
    private readonly LibraryDb _db;
    private readonly AiEngine _ai;
    private ClipModelSpec _model;
    private float _searchMinZ, _similarMinScore;
    private int _searchMaxResults, _tileSize, _gpuDevice;
    private bool _useGpu;
    private int _videoInterval;
    private int _thumbSize, _thumbQuality;
    private string _theme = "system";

    public const int DefaultTileSize = 168;

    /// <summary>Raised after the AI model changes, so views showing model state can refresh.</summary>
    public event Action? ModelChanged;

    public AppSettings(LibraryDb db, AiEngine ai)
    {
        _db = db;
        _ai = ai;
        _model = ClipModelCatalog.Find(db.GetSetting("ai_model")) ?? ClipModelCatalog.Default;
        _searchMinZ = GetFloat("ai_search_min_z", ai.SearchMinZ);
        _searchMaxResults = GetInt("ai_search_max_results", ai.SearchMaxResults);
        _similarMinScore = GetFloat("ai_similar_min_score", ai.SimilarMinScore);
        _tileSize = GetInt("tile_size", DefaultTileSize);
        _useGpu = _db.GetSetting("ai_use_gpu") == "1";
        _gpuDevice = GetInt("ai_gpu_device", 0);
        _videoInterval = Math.Clamp(GetInt("ai_video_interval", 10), 2, 60);
        _thumbSize = GetInt("thumb_size", 384);
        _theme = _db.GetSetting("theme") is "light" or "dark" ? _db.GetSetting("theme")! : "system";
        _thumbQuality = Math.Clamp(GetInt("thumb_quality", 88), 50, 100);
        ai.VideoFrameInterval = _videoInterval;
        ai.SetModel(_model);
        ai.SetRuntime(new ClipRuntime(_useGpu, _gpuDevice));
        ai.SearchMinZ = _searchMinZ;
        ai.SearchMaxResults = _searchMaxResults;
        ai.SimilarMinScore = _similarMinScore;
    }

    public ClipModelSpec Model
    {
        get => _model;
        set
        {
            if (_model.Id == value.Id) return;
            _model = value;
            _db.SetSetting("ai_model", value.Id);
            _ai.SetModel(value);
            ModelChanged?.Invoke();
        }
    }

    public bool UseGpu
    {
        get => _useGpu;
        set { _useGpu = value; _db.SetSetting("ai_use_gpu", value ? "1" : "0"); _ai.SetRuntime(new ClipRuntime(_useGpu, _gpuDevice)); }
    }

    public int GpuDevice
    {
        get => _gpuDevice;
        set { _gpuDevice = value; Set("ai_gpu_device", value); _ai.SetRuntime(new ClipRuntime(_useGpu, _gpuDevice)); }
    }

    /// <summary>Standard deviations above the mean a text-search hit must score. Lower is looser.</summary>
    public float SearchMinZ
    {
        get => _searchMinZ;
        set { _searchMinZ = value; _ai.SearchMinZ = value; Set("ai_search_min_z", value); }
    }

    public int SearchMaxResults
    {
        get => _searchMaxResults;
        set { _searchMaxResults = value; _ai.SearchMaxResults = value; Set("ai_search_max_results", value); }
    }

    public float SimilarMinScore
    {
        get => _similarMinScore;
        set { _similarMinScore = value; _ai.SimilarMinScore = value; Set("ai_similar_min_score", value); }
    }

    /// <summary>Seconds between the video frames the AI index samples.</summary>
    public int VideoFrameInterval
    {
        get => _videoInterval;
        set { _videoInterval = value; _ai.VideoFrameInterval = value; Set("ai_video_interval", value); }
    }

    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme
    {
        get => _theme;
        set { _theme = value; _db.SetSetting("theme", value); ApplyTheme(value); }
    }

    public static void ApplyTheme(string theme)
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    /// <summary>Short-edge pixels of generated thumbnails. Changing it means rebuilding the cache.</summary>
    public int ThumbnailSize
    {
        get => _thumbSize;
        set { _thumbSize = value; Set("thumb_size", value); }
    }

    /// <summary>JPEG quality of generated thumbnails.</summary>
    public int ThumbnailQuality
    {
        get => _thumbQuality;
        set { _thumbQuality = value; Set("thumb_quality", value); }
    }

    /// <summary>Edge length of a thumbnail tile in the grids, in pixels.</summary>
    public int TileSize
    {
        get => _tileSize;
        set { _tileSize = value; Set("tile_size", value); }
    }

    // ---- vault security

    /// <summary>Argon2id strength used for new vaults and password changes.</summary>
    public PhotoLibrary.Core.Vault.KdfPreset VaultKdf
    {
        get => PhotoLibrary.Core.Vault.KdfPreset.Find(_db.GetSetting("vault_kdf"));
        set => _db.SetSetting("vault_kdf", value.Id);
    }

    /// <summary>Minutes an unlocked vault stays open without use; 0 means until Keepsake closes.</summary>
    public int VaultAutoLockMinutes
    {
        get => Math.Max(0, GetInt("vault_autolock_min", 10));
        set => Set("vault_autolock_min", Math.Max(0, value));
    }

    // ---- library folders

    /// <summary>How dated folders are laid out. Changing it re-files date folders on the next Organise.</summary>
    public PhotoLibrary.Core.Layout.DateLayout DateLayout
    {
        get => PhotoLibrary.Core.Layout.DateLayout.Find(_db.GetSetting(PhotoLibrary.Core.Layout.FolderMap.SettingKey));
        set => _db.SetSetting(PhotoLibrary.Core.Layout.FolderMap.SettingKey, value.Id);
    }

    // ---- backup

    /// <summary>Folder backups go to, or null when backup isn't set up.</summary>
    public string? BackupFolder
    {
        get => _db.GetSetting("backup_folder") is { Length: > 0 } f ? f : null;
        set => _db.SetSetting("backup_folder", value ?? "");
    }

    /// <summary>"manual", "startup", "daily" or "weekly".</summary>
    public string BackupSchedule
    {
        get => _db.GetSetting("backup_schedule") is "manual" or "startup" or "weekly" ? _db.GetSetting("backup_schedule")! : "daily";
        set => _db.SetSetting("backup_schedule", value);
    }

    /// <summary>
    /// "encrypted" or "plain". Someone who already backs up to a folder keeps the plain copy they set up; a new
    /// backup is encrypted unless they choose otherwise.
    /// </summary>
    public string BackupFormat
    {
        get => _db.GetSetting("backup_format") is "encrypted" or "plain" ? _db.GetSetting("backup_format")! : BackupFolder is null ? "encrypted" : "plain";
        set => _db.SetSetting("backup_format", value);
    }

    /// <summary>Days an encrypted backup keeps older versions; 0 keeps them forever.</summary>
    public int BackupKeepDays
    {
        get => Math.Max(0, GetInt("backup_keep_days", 365));
        set => Set("backup_keep_days", Math.Max(0, value));
    }

    /// <summary>
    /// The encrypted backup's key, protected for this Windows user (DPAPI), so scheduled backups run without asking.
    /// Paired with the id of the backup it belongs to.
    /// </summary>
    public (string BackupId, string ProtectedKey)? BackupKey
    {
        get => _db.GetSetting("backup_key_id") is { Length: > 0 } id && _db.GetSetting("backup_key") is { Length: > 0 } k ? (id, k) : null;
        set
        {
            _db.SetSetting("backup_key_id", value?.BackupId ?? "");
            _db.SetSetting("backup_key", value?.ProtectedKey ?? "");
        }
    }

    /// <summary>When the last backup finished without errors.</summary>
    public DateTime? LastBackupUtc
    {
        get => DateTime.TryParse(_db.GetSetting("backup_last_ok"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
        set => _db.SetSetting("backup_last_ok", value?.ToString("O", CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>One-line outcome of the last backup attempt, successful or not.</summary>
    public string? LastBackupSummary
    {
        get => _db.GetSetting("backup_last_summary") is { Length: > 0 } s ? s : null;
        set => _db.SetSetting("backup_last_summary", value ?? "");
    }

    /// <summary>Shows tools for looking inside Keepsake's data, such as the database browser.</summary>
    public bool AdvancedMode
    {
        get => _db.GetSetting("advanced_mode") == "1";
        set => _db.SetSetting("advanced_mode", value ? "1" : "0");
    }

    private float GetFloat(string key, float fallback) =>
        float.TryParse(_db.GetSetting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private int GetInt(string key, int fallback) =>
        int.TryParse(_db.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void Set(string key, IFormattable value) => _db.SetSetting(key, value.ToString(null, CultureInfo.InvariantCulture));
}
