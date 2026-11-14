using Avalonia.Controls;
using Avalonia.Interactivity;
using PhotoLibrary.App.ViewModels;

namespace PhotoLibrary.App.Views;

/// <summary>Pick a backup version and restore it into a folder.</summary>
public partial class RestoreWindow : Window
{
    public RestoreWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (Vm.IsRunning) e.Cancel = true; };
    }

    private RestoreViewModel Vm => (RestoreViewModel)DataContext!;

    /// <summary>Shows the window; true when files were restored into the library (so it should be rescanned).</summary>
    public static async Task<bool> ShowAsync(MainWindow owner, RestoreViewModel vm)
    {
        var win = new RestoreWindow { DataContext = vm };
        await win.ShowDialog(owner);
        return vm.Restored && vm.IntoLibrary;
    }

    private async void ChooseTargetClick(object? sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow main && await main.PickFolderAsync("Restore into which folder?", Vm.Target) is { } folder)
            Vm.Target = folder;
    }

    private void LibraryTargetClick(object? sender, RoutedEventArgs e) => Vm.Target = Vm.LibraryRoot;
    private void CancelRestoreClick(object? sender, RoutedEventArgs e) => Vm.Cancel();
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
