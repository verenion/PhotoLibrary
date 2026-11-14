namespace PhotoLibrary.Core.Layout;

/// <summary>
/// Folders with nothing in them: no files at all, apart from Windows' housekeeping files (desktop.ini, Thumbs.db…),
/// at any depth. Only such folders are ever offered for deletion, and the check is repeated just before deleting.
/// </summary>
public static class EmptyFolders
{
    /// <summary>True when the folder and everything under it holds no file other than housekeeping files.</summary>
    public static bool IsEmpty(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
                    .All(f => MediaKinds.IgnoredNames.Contains(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // can't see inside: not provably empty
        }
    }

    /// <summary>
    /// Deletes the folder if it is still empty (see <see cref="IsEmpty"/>), housekeeping files and empty sub-folders
    /// included. Returns null on success, or why it was left alone.
    /// </summary>
    public static string? Delete(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        if (!IsEmpty(dir)) return $"“{Path.GetFileName(dir)}” isn't empty any more, so it was left alone.";
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                         .OrderByDescending(d => d.Length).ToList())
            {
                new DirectoryInfo(d).Attributes = FileAttributes.Directory;
                Directory.Delete(d);
            }
            new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
            Directory.Delete(dir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"“{Path.GetFileName(dir)}” couldn't be deleted: {ex.Message}";
        }
    }
}
