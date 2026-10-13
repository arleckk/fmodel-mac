using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FModel.Services;

namespace FModel.Views;

/// <summary>Adds or edits a favorite directory (FModel's CustomDir window).</summary>
public partial class FavoriteDirectoryWindow : Window
{
    private readonly Func<string, bool> _folderExists;

    public FavoriteDirectory? Result { get; private set; }

    // Designer only
    public FavoriteDirectoryWindow() : this(new FavoriteDirectory(), _ => true) { }

    public FavoriteDirectoryWindow(FavoriteDirectory favorite, Func<string, bool> folderExists)
    {
        _folderExists = folderExists;
        InitializeComponent();
        HeaderBox.Text = favorite.Header;
        PathBox.Text = favorite.Path;
        Opened += (_, _) => (string.IsNullOrEmpty(favorite.Header) ? HeaderBox : PathBox).Focus();
        UpdateHint();
    }

    private void OnPathChanged(object? sender, TextChangedEventArgs e) => UpdateHint();

    // A missing folder is allowed (another game version may have it), but it's worth knowing
    private void UpdateHint()
    {
        var path = FavoriteDirectory.Normalize(PathBox.Text ?? "");
        OkButton.IsEnabled = path.Length > 0;
        if (path.Length == 0)
        {
            PathHint.Text = "A folder path as shown in the Folders tab, separated by '/'.";
            PathHint.Foreground = (IBrush?) this.FindResource("AdonisDisabledForegroundBrush");
            return;
        }
        var exists = _folderExists(path);
        PathHint.Text = exists ? "Found in the loaded game" : "Not in the loaded game: the menu will say so when you go to it";
        PathHint.Foreground = exists ? new SolidColorBrush(Color.Parse("#66BB6A")) : (IBrush?) this.FindResource("AdonisAlertBrush");
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var path = FavoriteDirectory.Normalize(PathBox.Text ?? "");
        if (path.Length == 0) return;
        var header = string.IsNullOrWhiteSpace(HeaderBox.Text) ? path[(path.LastIndexOf('/') + 1)..] : HeaderBox.Text.Trim();
        Result = new FavoriteDirectory(header, path);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
