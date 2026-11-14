using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace PhotoLibrary.App.Views;

/// <summary>Shows a new backup's recovery key once, and won't close until the user says it's saved.</summary>
public partial class RecoveryKeyWindow : Window
{
    private bool _canClose;

    public RecoveryKeyWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!_canClose) e.Cancel = true; };
    }

    public static Task ShowAsync(Window owner, string recoveryKey)
    {
        var win = new RecoveryKeyWindow();
        win.KeyText.Text = recoveryKey;
        return win.ShowDialog(owner);
    }

    private async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(KeyText.Text ?? "");
            CopyButton.Content = "Copied";
        }
    }

    private void SavedChanged(object? sender, RoutedEventArgs e) => DoneButton.IsEnabled = SavedBox.IsChecked == true;

    private void DoneClick(object? sender, RoutedEventArgs e)
    {
        _canClose = true;
        Close();
    }
}
