<p align="center">
  <img src="logo.webp" alt="Keepsake" width="440">
</p>

# Keepsake

> **Work in progress.** It works on the author's library every day, but expect rough edges, schema changes
> and missing features. Issues and pull requests are welcome.

Keepsake is a private, local-only photo organiser. It sorts, searches, cleans up and secures a photo and video
library entirely on your own computer: no account, no cloud, no telemetry, and no internet connection needed to
use it.

The goal is a simple application that can organise, manage and protect a whole photo library with zero network
access. Everything it learns about your photos stays in a database on your machine, the AI runs on your own CPU
or GPU, and sensitive photos can be locked away in encrypted vaults.

## Highlights

- **Works offline.** Nothing about your photos ever leaves the machine. See [Privacy](#privacy) for the two
  optional downloads and how to avoid even those.
- **Organises without touching your photos.** Files are filed into dated folders, and every extra detail
  (dates, GPS, tags, AI vectors) lives in Keepsake's own database. Photos themselves are never edited.
- **On-device AI search.** Type "kids on the beach" or "red car 2019" and find matching photos and moments
  inside videos, using a CLIP model running locally.
- **Albums three ways.** Virtual albums are lists, folder albums are real folders on disk, and secure albums are
  encrypted vaults with their own password.
- **Backups that can't eat themselves.** Copy the library and its database to another drive, on a schedule or on
  demand. Old versions and deleted files are archived in the backup, never removed.
- **Clean-up tools.** Find exact and near duplicates, and blurry, dark or blown-out shots, and remove them safely.
- **Your files stay yours.** Plain folders on disk, nothing proprietary. Stop using Keepsake and your library is
  still a normal folder of photos.

## Features

### Library

- Year › Month › Day tree beside a thumbnail grid grouped by day. Click a photo for a large preview with its
  capture date (and where that date came from), camera, size, GPS and Google Takeout description.
- The bar above the grid sorts and filters it.
  - **Sort:** newest first, oldest first, name, largest files first or most pixels first. The date orders keep the day headings.
  - **Filters:** file type, camera, location, file size, megapixels, shape (landscape, portrait, square), album and file name.
  - Each album has the same bar, with its own settings, so filtering an album doesn't change the Library view.
- Each tile shows coloured dots for the albums the photo is in, and videos show their length.
- Right-click any photo, or a Ctrl/Shift-click selection, to open it, show it in its folder, find similar photos,
  add it to an album, remove it from the library or delete it.
  - Removing is a soft delete: the file stays on disk, is hidden, and is never re-imported. Restore from Settings.
  - Deleting from disk asks first and uses the Recycle Bin.

### Import and organise

- **Import folder…** checks every photo and video in any folder, Google Takeout exports included, against the
  library:
  - *Present*: an identical file is already there.
  - *Similar*: a re-saved, edited or resized version of a photo you have. Shown side by side so you decide.
  - *Missing*: copied into its dated folder. The source folder is never changed.
- **Organise…** previews, then files photos into date folders, `2024\2024_05\2024_05_31\<original name>` by
  default. Other layouts can be chosen in *Settings › Library folders*, such as `2024\05\31`, `2024-05-31`, or
  a folder per month or year.
  - Undated files go to `_Undated`. Duplicates are filed like any other photo; the Duplicates tab finds them.
  - Folder albums are left alone (see [Library folders](#library-folders)).
  - Every run is logged and can be undone.
- Capture dates come from, in order: camera EXIF or video metadata, a date and time in the file name, other EXIF
  dates, Google Takeout metadata, then a date-only file name.

### AI tools (on device)

- Uses OpenAI's CLIP model running locally through ONNX Runtime. Six sizes are available, from a 150 MB
  quick model to a large, more accurate one.
- **Search in plain English**, optionally with a year: `birthday cake`, `snow mountains 2016`, `receipt`.
- **Find similar** photos to any photo, and browse an automatic **tag cloud**.
- **Events** group the library by time, place and appearance.
- **Videos** are indexed from a frame every few seconds, so a search finds the moment that matches. The result
  shows "▶ 1:20 / 3:10" and the preview opens on that frame.
- Optional **GPU acceleration** on any DirectX 12 graphics card through DirectML, with no CUDA install. Full
  precision models index about ten times faster on a GPU.

### Albums

Right-click photos anywhere and choose *Add to album*. Each album has a name, colour, cover and type:

| Type | What it is | A photo can be in |
|---|---|---|
| **Virtual** | A list in Keepsake's database. Files don't move. | Any number |
| **Folder** | A real folder at the top of the library. Adding a photo moves its file there; removing it moves it back. | One |
| **Secure** | An encrypted vault with its own password. | One |

### Paired files

Some shots are saved as more than one file. Keepsake shows each of these shots as a single tile, with a badge such as `RAW+JPG` or `LIVE`:

- **RAW + JPEG:** for example `DSC_0001.JPG` and `DSC_0001.NEF`.
- **Pixel RAW:** `PXL_….RAW-01.COVER.jpg` and `PXL_….RAW-02.ORIGINAL.dng`.
- **Pixel portrait:** the `COVER` and `ORIGINAL` versions.
- **Live Photos:** for example `IMG_1234.HEIC` and `IMG_1234.MOV`.
- **Pixel motion photos from Google Takeout:** `….MP.COVER.jpg` and its `….MP.COVER` clip.

To count as one shot, the files must be in the same folder and share a name. A clip counts only if it's 10 seconds or shorter. Anything ambiguous, such as `IMG_1.JPG` with `IMG_1.PNG`, is left as separate files.

The extra files go wherever the photo goes:
- **Organise, Import, albums and vaults** move, copy, encrypt or restore them together. If a name is already taken, the whole shot is renamed (`IMG_1_1.HEIC` with `IMG_1_1.MOV`).
- **Importing a RAW or clip on its own:** if its photo is already in the library, it's placed next to that photo.
- **Removing or deleting a shot** from the grid removes all of its files.
- **The Duplicates tab** removes only the files you tick, and never matches a file against another part of its own shot. When choosing which copy to keep, it prefers the copy that has its RAW or clip.

### Library folders

Every top-level folder in the library is one of these:

- **Date folders** are where Organise files photos by date. Keepsake only treats a folder as one if it created it,
  or you said so.
- **Folder albums** are every other folder, including ones you make in Explorer or that another app writes to,
  such as a phone's `Camera Roll`. Each becomes an album after a rescan once it has a photo in it. Organise
  never moves anything out of them unless you switch on *Organise this folder* in that album's settings.
- **Keepsake's own folders** start with `_`. Folders starting with `$` or `.` belong to sync tools and are left
  alone.

Keepsake never decides from a name alone. A folder it didn't create but that's named like a date, such as
`2012`, `2012-05-31` or `2012_05`, waits for you. A *some folders look like dates* button appears, and you choose
*Date folder* or *Folder album* for each one. The review shows which date layout the folder's files already
follow, and suggests that layout if it differs from the setting. Until you decide, Organise and Import leave such
folders alone. *Settings › Library folders › Review folders…* lets you change any folder later. Changing a
folder's role, or the date layout, never moves anything by itself: the next Organise does the re-filing.

Folders that are completely empty also get a *Delete empty folder* option in the review, and it's chosen for them by default. Such a folder can contain empty sub-folders and Windows files like `desktop.ini`, but nothing else. A folder with no photos that still holds other files, such as documents, is never offered for deletion. Deleting happens when you press Save. Each folder is checked again just before it's deleted, and is left alone if anything has appeared in it since.

Albums made by older versions in `_Albums\<name>` keep working where they are.

### Secure vaults

For scanned documents, IDs and anything else you want kept private.

- Adding a photo encrypts it into the vault and checks the encrypted copy decrypts to the same bytes. Only then
  is the original deleted permanently, not to the Recycle Bin.
- Every trace of the photo leaves the rest of the library: its database entry, AI index entries, tags, album
  memberships, cached thumbnail and log entries.
- A locked vault shows only grey lock tiles. Once unlocked, thumbnails and previews are decrypted into memory
  only, never to disk.
- Vaults lock by hand, after a period without use (10 minutes by default; 1 minute to 1 hour, or never, in
  *Settings › Vault security*), and always when Keepsake closes. The key is wiped from memory.
- *Settings › Vault security* also sets how hard the password is to guess: Standard, Strong or Maximum Argon2id,
  with a button to time an unlock on your PC. It applies to new vaults and to password changes; re-entering the
  same password moves an existing vault to the new setting without re-encrypting any photos.
- From an unlocked vault you can export a decrypted copy, move a photo back into the library, or delete it.
- Each vault has its own password. **There is no recovery key: a forgotten password means those photos are gone.**

The encryption is described under [Vault format](#vault-format).

### Clean-up

- **Duplicates** groups copies at four levels of risk:
  - Byte-identical files.
  - Re-saved copies.
  - Near-identical shots.
  - Merely similar shots, when the library is AI-indexed.

  Each group keeps its best copy and ticks the rest for removal. A RAW file is always kept over its JPEG twin.
- **Quality** measures brightness, contrast and sharpness once, then a strictness slider surfaces black,
  blown-out, flat and blurry photos, worst first, with the reason under each.

### Backup

Set up backups in *Settings › Backup*:
1. Pick a format.
2. Pick a folder, ideally on another drive or in a cloud folder such as OneDrive.
3. Choose when to back up: only when you click *Back up now*, each time Keepsake opens, once a day or once a week.

Only new and changed files are copied, so after the first run a backup takes seconds to minutes. Backups run while Keepsake is open.

#### Encrypted (recommended)

The backup folder can't be read without your password. That makes it safe to keep in OneDrive, Dropbox or on a drive that could be lost.

**What's protected:**
- **File contents:** each distinct file is encrypted as its own object, in the same format as vaults: AES-256-GCM, with a separate key for each file.
- **Names, folders and dates:** these exist only inside encrypted *version lists*. Objects are named by a keyed hash, so nobody can check whether a particular known photo is in the backup.
- Someone with the folder sees only how many objects there are and how big each one is.

**Compression:** files are compressed (Brotli) when that saves space, such as the database and some RAW formats. Photos and videos are already compressed, so they're stored as they are.

**Identical files** are stored once, however many copies the library has.

**Password and recovery key:**
- When you set up an encrypted backup, you choose a password and Keepsake shows a **recovery key** once. Either one opens the backup.
- Keepsake remembers the backup's key on this PC, protected by your Windows sign-in (DPAPI), so scheduled backups run without asking.
- On another PC, enter the password or recovery key once to connect to the backup.
- *Change password…* takes the current password or the recovery key. The recovery key stays the same.
- **Without the password or the recovery key, nobody can restore the backup, including you.**

**Versions:**
- Each backup run writes a version: a list of the whole library as it was at that moment.
- A photo you delete, or that gets damaged, stays in the earlier versions.
- *Keep earlier versions* (30 days, 90 days, 1 year or forever; 1 year by default) decides how far back you can go. After each backup, older versions are dropped, along with files that only those versions used.
- The newest version is always kept.

**Restore… and Check backup:**
- *Restore…* lets you pick a version, a folder to restore into and, optionally, only paths containing some text, such as `2019` or `Camera Roll`.
- Restoring never overwrites a file. Choosing *The library* as the destination brings back only what's missing.
- Every restored file is checked against the fingerprint taken when it was backed up.
- *Check backup* decrypts every file in the newest version and checks it, without writing anything.

The rest of this section describes the plain copy.

#### Plain copy

- The backup has the same folders as the library, so it's an ordinary folder of photos you can browse or
  restore by copying back. Nothing is encrypted.
- Nothing in the backup is deleted or overwritten. When a file changes, or is moved or deleted in the library,
  the old backup copy moves to `_Archive\<date and time>\`. Delete old archive folders yourself when you need
  the space.
- Vaults are copied as they are, still encrypted. Their passwords are needed to open them from the backup too.
- `_Keepsake\library.sqlite` is a copy of Keepsake's database (albums, AI index, tags and settings). To restore
  it, copy it to `%LOCALAPPDATA%\PhotoLibrary\` while Keepsake is closed.
- If a library folder can't be read, or the library is missing or empty, nothing is archived that run, so an
  unplugged drive never looks like deleted photos.

### Formats

JPEG, PNG, WebP, GIF, BMP, HEIC, TIFF, Photoshop PSD/PSB, RAW (CR2, CR3, DNG, NEF, ARW, ORF, RW2, RAF) and
common video formats (MP4, MOV, M4V, 3GP, AVI, MTS, MKV, WMV), including Pixel motion photos from Google Takeout.

- **HEIC** and **TIFF** use Windows' own decoders. HEIC needs the free "HEIF Image Extensions" and "HEVC Video
  Extensions" from the Microsoft Store, which most PCs already have.
- **RAW** previews come from the camera's embedded JPEG, turned upright. A DNG with no preview is rendered from
  its sensor data.
- **PSD** previews come from the flattened image Photoshop stores inside the file.
- **Video** thumbnails, previews and AI indexing need ffmpeg (see [Privacy](#privacy)).

### Settings

Settings is split into sections, listed down the left:

| Section | What's in it |
|---|---|
| **General** | Library folder, restoring removed photos, the date folder layout, updates, and Advanced mode |
| **Appearance** | Theme, tile size, thumbnail resolution and quality |
| **Backup** | Backup folder and schedule |
| **Security** | Vault password strength and auto-lock |
| **AI** | AI model, video frame sampling, GPU and search tuning |
| **Video and HEIC** | ffmpeg and HEIC support |
| **Storage** | What Keepsake stores. Each row has a button to clear the thumbnail cache, delete a model you're not using, delete an AI index or clear quality measurements. |
| **Database** | Shown only in Advanced mode; see below |

**Database browser.** Switch on *Advanced mode* in General to add the Database section. It's a read-only look at what Keepsake has stored:
- **Photos:** search by name, folder or id. For each photo it shows:
  - every AI label, with the model that gave it and its confidence score;
  - its search index entry, video frames, quality measurements, events, albums and paired files;
  - its raw row in the files table.
- **Labels:** every label a model has applied, with how many photos carry it. Pick a label to see those photos, most confident first. Double-click a photo to inspect it.
- **Tables:** every table with its row count, plus a box for running SQL queries.

The browser uses its own read-only database connection. A query that tries to change data fails, so nothing in this section can alter your library.

## Privacy

Keepsake makes **no network connections on its own**. There are three, each made only when you click its
button:

| Connection | To | Needed for |
|---|---|---|
| Download the CLIP AI model, 150 MB to 1.7 GB | Hugging Face (`huggingface.co/Xenova/...`) | AI search, tags, events, similar photos |
| Download ffmpeg, about 200 MB | GitHub (`BtbN/FFmpeg-Builds`) | Video thumbnails, previews and AI indexing |
| *Check for updates* in Settings | GitHub Releases for this repo | Updating Keepsake |

Everything else works without any of them. There is no telemetry, no automatic update check and no account.

**Fully offline machines** can install both by hand:

- **The AI model:** copy the four files of the model you want into
  `%LOCALAPPDATA%\PhotoLibrary\ai_models\<model id>\`. Each comes from `https://huggingface.co/Xenova/<repo>`:
  - `vision_model_quantized.onnx` and `text_model_quantized.onnx`, from that repo's `onnx` folder. Use the
    `_quantized` versions for the int8 ids, and `vision_model.onnx` and `text_model.onnx` for the others.
  - `vocab.json` and `merges.txt`.

  | Model id | Repo |
  |---|---|
  | `clip-vit-base-patch32-q8`, `clip-vit-base-patch32` | `clip-vit-base-patch32` |
  | `clip-vit-base-patch16-q8`, `clip-vit-base-patch16` | `clip-vit-base-patch16` |
  | `clip-vit-large-patch14-q8`, `clip-vit-large-patch14` | `clip-vit-large-patch14` |

- **ffmpeg:** put `ffmpeg.exe` in `%LOCALAPPDATA%\PhotoLibrary\tools\` or anywhere on your `PATH`. Then click
  *Check again* in Settings.

What Keepsake keeps, all on your machine:

- `%LOCALAPPDATA%\PhotoLibrary\library.sqlite` holds the index of your library, AI vectors and tags, albums and
  settings. Deleted entries are overwritten, not just unlinked.
- `%LOCALAPPDATA%\PhotoLibrary\thumbs\` holds the thumbnail cache. Vault photos are never cached.
- Inside your library: your date folders and folder albums, plus `_Vault` (encrypted vaults),
  `_Undated` and `_Logs`.

## Download

From the [Releases](https://github.com/verenion/PhotoLibrary/releases) page, get either:

- **`Keepsake-win-Setup.exe`**, the installer. It installs for your Windows user only, in
  `%LOCALAPPDATA%\Keepsake`, with no admin prompt, and adds a Start menu shortcut. Uninstall from Windows
  *Settings › Apps*.
- **`Keepsake-win-Portable.zip`**: unzip anywhere and run `Keepsake.exe`. Nothing is installed.

Both are self-contained, so .NET doesn't need to be installed, and both have a `.sha256` file beside them to
check the download. The app isn't code-signed yet, so Windows SmartScreen may warn the first time: choose
*More info › Run anyway*.

**Updating:** open *Settings › Updates* and click *Check for updates*. If there's a newer release, Keepsake
downloads it (usually just the changed parts, a few MB), then *Restart to update* installs it and reopens.
Keepsake never checks by itself. Your library, database, AI models and settings live outside the app folder, so
updating, reinstalling or uninstalling doesn't touch them.

## Releasing

Pushing a version tag builds, tests and publishes a release. Nothing else does.

```
git tag v0.1.0
git push origin v0.1.0
```

The workflow packs the app with [Velopack](https://velopack.io) into the installer, the portable zip and the
update packages. A delta from the previous release is included when there is one. A tag with a suffix, such as
`v0.2.0-beta.1`, becomes a pre-release, which *Check for updates* ignores. Release notes are generated from the
commits since the previous tag. Every push to `main` and every pull request is also built and tested, but
documentation-only changes are skipped.

## Run

Requires Windows 10 or 11 and the .NET 10 SDK.

```
dotnet run --project src\PhotoLibrary.App
```

Open `PhotoLibrary.slnx` in Visual Studio, Rider or VS Code. The `.vscode` folder has launch and build tasks.
Build output goes to `%LOCALAPPDATA%\PhotoLibraryBuild`, outside the source tree.

The UI is built with Avalonia, which is cross-platform, but Keepsake is only tested on Windows. HEIC and TIFF
decoding, GPU acceleration and the Recycle Bin use Windows components.

## Project layout

- `src/PhotoLibrary.Core` covers the index, metadata, hashing, import, organise, albums, vaults and image
  decoding. It has no UI.
- `src/PhotoLibrary.AI` covers the CLIP model wrapper, vector index, tagger, events, duplicates and quality.
- `src/PhotoLibrary.App` is the Avalonia desktop app.
- `tests/PhotoLibrary.Tests` holds the xunit tests. AI tests skip themselves when no model is downloaded.

The code and data folders still use the working name `PhotoLibrary`.

## Vault format

Each vault is a folder `_Vault\<random id>` holding `vault.json` and one `<random>.plv` file per photo.

- **Password:** it goes through Argon2id (64 MiB and 3 passes by default, up to 1 GiB and 4 passes, always 4
  lanes; recorded in `vault.json` so each vault can be raised later). The result unwraps a random 256-bit vault key with AES-256-GCM. Changing the password re-wraps
  that key; no photo is re-encrypted.
- **Files:** each `.plv` has a random salt. HKDF-SHA256 derives a per-file key from it and the vault key. The
  photo's details (original name, dates, camera, GPS) and its contents are sealed with AES-256-GCM in 1 MiB
  records. Each record's nonce carries its index and a final-record flag, so altered, reordered or truncated
  files are rejected.
- **What's visible on disk:** only how many items a vault holds and roughly their sizes.

**Known limits:**

- Videos show no thumbnail inside a vault, because decoding them would need a temporary decrypted file.
- Identical copies of a photo elsewhere in the library stay unencrypted. The confirmation says how many.
- Importing the same photo again from another folder would bring it back unencrypted.
- Old Organise and Import logs in `_Logs` may still name the file.
- Deleted originals may still be recoverable from an SSD's raw storage or Windows' own thumbnail cache.

## Tests

```
dotnet test
```

`IntegrationTests` uses real camera JPEGs from the folder named by the `PHOTOLIBRARY_SAMPLES` environment
variable when it is set, and generated images otherwise.

## Licence

MIT, see [LICENSE](LICENSE). Third-party components and the optional downloads are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
