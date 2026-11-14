using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void RefreshFfmpegClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.RefreshFfmpeg();
    }

    private async void ReviewFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || window.DataContext is not MainViewModel main) return;
        if (main.IsBusy) return;
        await FolderReviewWindow.ShowAsync(window, main, onlyUndecided: false);
    }

    private async void ChooseBackupFolderClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || window.DataContext is not MainViewModel main) return;
        var folder = await window.PickFolderAsync(main.Backup.IsEncrypted
            ? "Choose an empty folder for the encrypted backup, or an existing one"
            : "Choose where backups go", main.Backup.Folder);
        if (folder is not null) await main.Backup.SetFolderAsync(folder);
    }

    private async void RestoreClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || window.DataContext is not MainViewModel main) return;
        if (main.Backup.Folder is not { } folder || main.Backup.KeyForRestore() is not { } key) return;
        RestoreViewModel vm;
        try { vm = new RestoreViewModel(folder, key, main.State.LibraryRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            main.StatusText = "The backup can't be read: " + ex.Message;
            return;
        }
        using (vm)
        {
            if (await RestoreWindow.ShowAsync(window, vm)) await main.RescanAsync();
        }
    }

    private void CancelBackupClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) main.Backup.Cancel();
    }

    private async void ChangeLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || window.DataContext is not MainViewModel main) return;
        if (main.IsBusy) return;
        var folder = await window.PickFolderAsync("Choose the photo library folder", main.State.LibraryRoot);
        if (folder is not null) await main.ChangeLibraryAsync(folder);
    }
}
