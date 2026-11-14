using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhotoLibrary.App.Views;

/// <summary>Asks for a new password twice. Returns it, or null when cancelled.</summary>
public partial class NewPasswordWindow : Window
{
    public const int MinLength = 10;

    public NewPasswordWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string message)
    {
        var win = new NewPasswordWindow { Title = title };
        win.Heading.Text = title;
        win.Message.Text = message;
        return await win.ShowDialog<string?>(owner);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        var pw = PasswordBox.Text ?? "";
        string? problem = pw.Length < MinLength ? $"Use at least {MinLength} characters."
            : pw != (ConfirmBox.Text ?? "") ? "The two passwords don't match."
            : null;
        if (problem is null) { Close(pw); return; }
        Error.Text = problem;
        Error.IsVisible = true;
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
