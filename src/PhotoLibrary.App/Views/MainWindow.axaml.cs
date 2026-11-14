using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Confirm = (title, message, confirmText) => ConfirmWindow.ShowAsync(this, title, message, confirmText);
                vm.PromptPassword = (title, message, validate) => PasswordWindow.ShowAsync(this, title, message, validate);
                vm.PromptNewPassword = (title, message) => NewPasswordWindow.ShowAsync(this, title, message);
                vm.ShowRecoveryKey = key => RecoveryKeyWindow.ShowAsync(this, key);
                Closing += (_, _) => { vm.Vaults.LockAll(); vm.Backup.Cancel(); };
                // A new tree selection replaces the groups: start the grid from the top each time.
                vm.Groups.CollectionChanged += (_, e) =>
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) GridScroll.ScrollToHome();
                };
                var init = vm.InitializeAsync();
                vm.Backup.Start();
                await init;
                // Folders named like dates that Keepsake didn't make: ask once per session rather than guess.
                if (vm.HasUndecidedFolders) await FolderReviewWindow.ShowAsync(this, vm, onlyUndecided: true);
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private void TabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender) || DataContext is not MainViewModel vm) return;
        // Counts in the date tree go stale when photos are removed from another tab; refresh on return.
        if (vm.SelectedTab == MainViewModel.LibraryTab) vm.RefreshTreeIfStale();
        if (vm.SelectedTab == MainViewModel.AlbumsTab) vm.Albums.Reload();
        if (vm.SelectedTab == MainViewModel.SettingsTab) vm.Settings.RefreshLibrary();
    }

    public async Task<string?> PickFolderAsync(string title, string? start = null)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is not null && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>Import and Organise file photos by date, so undecided folders have to be settled first.</summary>
    private async Task<bool> FoldersDecidedAsync()
    {
        if (!Vm.HasUndecidedFolders) return true;
        await FolderReviewWindow.ShowAsync(this, Vm, onlyUndecided: true);
        if (!Vm.HasUndecidedFolders) return true;
        Vm.StatusText = "Choose whether the folders named like dates are date folders or albums first.";
        return false;
    }

    private async void ReviewFoldersClick(object? sender, RoutedEventArgs e) =>
        await FolderReviewWindow.ShowAsync(this, Vm, onlyUndecided: true);

    private async void ImportClick(object? sender, RoutedEventArgs e)
    {
        if (!await FoldersDecidedAsync()) return;
        var vm = new ImportViewModel(Vm.State, Vm);
        var win = new ImportWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void OrganiseClick(object? sender, RoutedEventArgs e)
    {
        if (!await FoldersDecidedAsync()) return;
        var vm = new OrganiseViewModel(Vm.State, Vm);
        vm.LoadPreviousRuns();
        var win = new OrganiseWindow { DataContext = vm };
        await win.ShowDialog(this);
    }

    private async void ChangeLibraryClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Choose the photo library folder", Vm.State.LibraryRoot);
        if (folder is not null) await Vm.ChangeLibraryAsync(folder);
    }
}
