using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CUE4Parse.UE4.Versions;
using FModel.Services;

namespace FModel.Views;

/// <summary>
/// FModel's Directory Selector, plus profiles: pick a saved game to switch to (or edit its directory, version and
/// name), or "New Profile…" for another game. Everything else of a profile (keys, mappings…) is set as usual once
/// it's the current game. Changes apply on OK; the caller saves and mounts.
/// </summary>
public partial class DirectorySelectorWindow : Window
{
    private const string NewProfile = "New Profile…";
    private static readonly string[] Names = Enum.GetNames<EGame>();

    private readonly AppSettings _settings;
    private readonly List<GameProfile> _profiles;
    private readonly HashSet<GameProfile> _deleted = [];

    // Designer only
    public DirectorySelectorWindow() : this(new AppSettings()) { }

    /// <param name="newProfile">Start on "New Profile…" instead of the current game.</param>
    public DirectorySelectorWindow(AppSettings settings, bool newProfile = false)
    {
        _settings = settings;
        _profiles = settings.Profiles.ToList();
        InitializeComponent();

        // Enum order keeps each game next to its engine version, like FModel's list
        Versions.ItemsSource = Names;
        RefreshProfiles(newProfile ? null : settings.ActiveProfile);
        // The name used when none is typed
        DirectoryBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                NameBox.Watermark = string.IsNullOrWhiteSpace(DirectoryBox.Text) ? "Profile name (e.g. the game)" : GameProfile.SuggestName(DirectoryBox.Text);
        };
    }

    private GameProfile? SelectedProfile => ProfileBox.SelectedIndex is var i && i >= 0 && i < _profiles.Count ? _profiles[i] : null;

    private void RefreshProfiles(GameProfile? select)
    {
        ProfileBox.ItemsSource = _profiles.Select(p => p == _settings.ActiveProfile ? $"{p.Name} (current)" : p.Name).Append(NewProfile).ToArray();
        ProfileBox.SelectedIndex = select is null ? _profiles.Count : _profiles.IndexOf(select);
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NameBox is null) return;
        ErrorText.IsVisible = false;
        if (SelectedProfile is { } profile)
        {
            // The current profile's latest values are the settings themselves
            var current = profile == _settings.ActiveProfile;
            NameBox.Text = profile.Name;
            SelectVersion(current ? _settings.Game : profile.Game);
            DirectoryBox.Text = current ? _settings.ArchiveDir : profile.ArchiveDir;
            ProfileText.Text = current ? "The game loaded now. Its keys, mappings and the rest are edited from Directory › AES and Settings."
                : $"Switches to this game: {profile.Summary}.";
            DeleteButton.IsEnabled = !current;
        }
        else
        {
            NameBox.Text = "";
            SelectVersion(_settings.Game);
            DirectoryBox.Text = "";
            ProfileText.Text = "A new game, with no keys or mappings yet: add them from Directory › AES and Settings once it's loaded. " +
                               "The current game stays saved as its own profile.";
            DeleteButton.IsEnabled = false;
        }
    }

    private void SelectVersion(string? game) =>
        Versions.SelectedItem = Names.FirstOrDefault(n => n.Equals(game, StringComparison.OrdinalIgnoreCase)) ?? nameof(EGame.GAME_UE5_LATEST);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile || profile == _settings.ActiveProfile) return;
        _deleted.Add(profile);
        _profiles.Remove(profile);
        RefreshProfiles(_settings.ActiveProfile);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(DirectoryBox.Text) ? await StorageProvider.TryGetFolderFromPathAsync(DirectoryBox.Text!) : null;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the game archive directory",
            SuggestedStartLocation = start
        });
        if (result.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        DirectoryBox.Text = path;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = GameProfile.SuggestName(path);
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var dir = DirectoryBox.Text?.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Fail("The directory does not exist");
            return;
        }
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? GameProfile.SuggestName(dir) : NameBox.Text.Trim();
        var profile = SelectedProfile;
        if (_profiles.FirstOrDefault(p => p != profile && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not null)
        {
            Fail($"There is already a profile named '{name}'");
            return;
        }

        foreach (var deleted in _deleted) _settings.Profiles.Remove(deleted);
        if (profile is null)
        {
            // A new game starts clean: no keys, mappings or endpoints from the previous one
            profile = new GameProfile { Name = name, ArchiveDir = dir, Game = Versions.SelectedItem as string ?? nameof(EGame.GAME_UE5_LATEST) };
            _settings.Profiles.Add(profile);
        }
        if (profile != _settings.ActiveProfile) _settings.ApplyProfile(profile);
        profile.Name = name;
        _settings.CurrentProfile = name;
        _settings.ArchiveDir = dir;
        _settings.Game = Versions.SelectedItem as string ?? nameof(EGame.GAME_UE5_LATEST);
        _settings.SyncActiveProfile();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
