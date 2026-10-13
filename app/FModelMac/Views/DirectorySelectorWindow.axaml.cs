using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CUE4Parse.UE4.Versions;
using FModelMac.Services;

namespace FModelMac.Views;

public partial class DirectorySelectorWindow : Window
{
    private readonly AppSettings _settings;

    // Designer only
    public DirectorySelectorWindow() : this(new AppSettings()) { }

    public DirectorySelectorWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        // Enum order keeps each game next to its engine version, like FModel's list
        var names = Enum.GetNames<EGame>();
        Versions.ItemsSource = names;
        Versions.SelectedItem = names.FirstOrDefault(n => n.Equals(settings.Game, StringComparison.OrdinalIgnoreCase))
                                ?? nameof(EGame.GAME_UE5_LATEST);
        DirectoryBox.Text = settings.ArchiveDir;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(DirectoryBox.Text) ? await StorageProvider.TryGetFolderFromPathAsync(DirectoryBox.Text!) : null;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the game archive directory",
            SuggestedStartLocation = start
        });
        if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) DirectoryBox.Text = path;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var dir = DirectoryBox.Text?.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            ErrorText.Text = "The directory does not exist";
            ErrorText.IsVisible = true;
            return;
        }

        _settings.ArchiveDir = dir;
        _settings.Game = Versions.SelectedItem as string ?? nameof(EGame.GAME_UE5_LATEST);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
