using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CUE4Parse.FileProvider.Objects;
using FModelMac.Rendering;
using FModelMac.Services;
using FModelMac.Services.Audio;
using FModelMac.ViewModels;
using FModelMac.Views;

namespace FModelMac;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private AssetTab? _observedTab;

    public MainWindow()
    {
        InitializeComponent();
        PackagesBreadcrumb.SegmentClicked += (_, path) => _vm?.NavigateToFolder(path);

        // Tunnel: the lists' ScrollViewer marks Left/Right (and the ListBox Enter) as handled
        // before a bubbling KeyDown handler would ever see them
        FolderList.AddHandler(KeyDownEvent, OnFolderKeyDown, RoutingStrategies.Tunnel);
        PackagesList.AddHandler(KeyDownEvent, OnPackageKeyDown, RoutingStrategies.Tunnel);
        FavoritesList.AddHandler(KeyDownEvent, OnFavoritesKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnHotkeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);

        // Settings › Themes recolors the shared JSON definition: redraw what's on screen
        AppResources.HighlightingChanged += OnHighlightingChanged;
        Closed += (_, _) => AppResources.HighlightingChanged -= OnHighlightingChanged;
    }

    private void OnHighlightingChanged() => Editor.TextArea.TextView.Redraw();

    private MainViewModel Vm => _vm ?? throw new InvalidOperationException("No view model");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Logs.CollectionChanged -= OnLogsChanged;
            _vm.ModelReady -= OnModelReady;
            _vm.MapReady -= OnMapReady;
            _vm.MaterialReady -= OnMaterialReady;
            _vm.SoundsReady -= OnSoundsReady;
            _vm.ReferencesReady -= OnReferencesReady;
            _vm.Favorites.CollectionChanged -= OnFavoritesChanged;
            _vm.MergerImageAdded -= OnMergerImageAdded;
            _vm.AnimationReady = null;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is null) return;

        _vm.SetClipboardText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        AppResources.SetJsonTheme(_vm.Settings.JsonHighlightTheme);
        ShowHotkeysInMenus();
        _vm.PickBackupFile = PickBackupFileAsync;
        _vm.ExportSession.ChooseStreamingLevels = filter =>
            new StreamingLevelFilterWindow(filter).ShowDialog(_exportSession is { IsVisible: true } session ? session : this);
        _vm.MergerImageAdded += OnMergerImageAdded;
        _vm.Favorites.CollectionChanged += OnFavoritesChanged;
        BuildFavoritesMenu();
        BuildProfilesMenu();
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Logs.CollectionChanged += OnLogsChanged;
        _vm.ModelReady += OnModelReady;
        _vm.MapReady += OnMapReady;
        _vm.MaterialReady += OnMaterialReady;
        _vm.SoundsReady += OnSoundsReady;
        _vm.ReferencesReady += OnReferencesReady;
        _vm.AnimationReady = animation => _viewer is { IsVisible: true } viewer
            ? viewer.PlayAnimation(animation)
            : "Open a skeletal mesh in the 3D viewer first, then double-click the animation";
        ObserveTab(_vm.SelectedTab);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm is null) return;

        // Like FModel's first run: ask for a game when none is configured yet
        if (_vm.HasGameDirectory) await _vm.InitializeGameAsync();
        else await ShowDirectorySelectorAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _viewer?.Close(); // independent window: it would otherwise keep the app alive
        _audioPlayer?.Close();
        _audio?.Dispose();
        if (_vm is null) return;

        var imageWidth = TabContent.ColumnDefinitions[2].ActualWidth;
        if (imageWidth > 50) _vm.Settings.ImagePanelWidth = imageWidth;
        _vm.SaveSettings();
    }

    // ---------- Image column (shown only when the tab has a texture) ----------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ObserveTab(Vm.SelectedTab);
    }

    private void ObserveTab(AssetTab? tab)
    {
        if (_observedTab is not null) _observedTab.PropertyChanged -= OnTabPropertyChanged;
        _observedTab = tab;
        if (tab is not null) tab.PropertyChanged += OnTabPropertyChanged;
        UpdateImageColumn();
        UpdateImageInterpolation();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetTab.HasImage)) UpdateImageColumn();
        if (e.PropertyName == nameof(AssetTab.ImageInterpolation)) UpdateImageInterpolation();
    }

    // Pixel art without smoothing (RenderOptions can't be bound)
    private void UpdateImageInterpolation() => Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(TabImage,
        _observedTab?.ImageInterpolation ?? Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

    private void UpdateImageColumn()
    {
        var column = TabContent.ColumnDefinitions[2];
        var current = column.ActualWidth;
        if (current > 50 && _vm is not null) _vm.Settings.ImagePanelWidth = current;

        column.Width = _observedTab?.HasImage == true
            ? new GridLength(_vm?.Settings.ImagePanelWidth ?? 360)
            : new GridLength(0);
    }

    // ---------- Console ----------

    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);

    // ---------- Menu ----------

    private async void OnDirectorySelectorClick(object? sender, RoutedEventArgs e) => await ShowDirectorySelectorAsync();

    private async Task ShowDirectorySelectorAsync() => await ShowDirectorySelectorAsync(newProfile: false);

    private async Task ShowDirectorySelectorAsync(bool newProfile)
    {
        var dialog = new DirectorySelectorWindow(Vm.Settings, newProfile);
        var ok = await dialog.ShowDialog<bool>(this);
        BuildProfilesMenu();
        if (!ok) return;
        Vm.SaveSettings();
        await Vm.InitializeGameAsync();
    }

    // Directory › Profiles: one item per saved game (the current one checked), then New / Edit
    private void BuildProfilesMenu()
    {
        if (_vm is null) return;
        var items = new List<Control>();
        foreach (var profile in _vm.Settings.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var current = profile == _vm.Settings.ActiveProfile;
            var item = new MenuItem
            {
                Header = profile.Name.Replace("_", "__"), // '_' marks an access key
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = current,
                GroupName = "profiles"
            };
            ToolTip.SetTip(item, $"{profile.ArchiveDir}\n{profile.Summary}");
            item.Click += async (_, _) =>
            {
                await Vm.SwitchProfileAsync(profile);
                BuildProfilesMenu();
            };
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        var add = new MenuItem { Header = "New Profile…" };
        add.Click += async (_, _) => await ShowDirectorySelectorAsync(newProfile: true);
        var edit = new MenuItem { Header = "Edit Profiles…" };
        edit.Click += async (_, _) => await ShowDirectorySelectorAsync(newProfile: false);
        items.Add(add);
        items.Add(edit);
        ProfilesMenu.ItemsSource = items;
    }

    private async void OnAesManagerClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AesManagerWindow(Vm.Settings, Vm.Archives);
        if (await dialog.ShowDialog<bool>(this))
        {
            Vm.SaveSettings();
            await Vm.InitializeGameAsync();
        }
    }

    private async void OnBackupClick(object? sender, RoutedEventArgs e) => await new BackupWindow(Vm).ShowDialog(this);

    // "All (New)" / "All (Modified)" ask for the backup to compare with, starting in Output/Backups
    private async Task<string?> PickBackupFileAsync()
    {
        var directory = GameBackup.Directory(Vm.Settings);
        var start = Directory.Exists(directory) ? await StorageProvider.TryGetFolderFromPathAsync(directory) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a backup file older than your current game version",
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("FBKP Files") { Patterns = ["*.fbkp"] }, FilePickerFileTypes.All]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnArchivesInfoClick(object? sender, RoutedEventArgs e)
    {
        foreach (var archive in Vm.Archives)
        {
            Vm.Log(archive.IsEnabled ? LogKind.Information : LogKind.Warning,
                $"{archive.Name} | {archive.FileCountText} | {archive.LengthText} | Mount Point: {archive.MountPoint} | " +
                $"Encrypted: {archive.IsEncrypted} | {(archive.IsEnabled ? "Mounted" : "Locked")}");
        }
        Vm.IsLoggerExpanded = true;
    }

    private SearchWindow? _search;

    private void OnSearchClick(object? sender, RoutedEventArgs e) => ShowSearch(SearchTab.ByPath);

    private void OnReferencesClick(object? sender, RoutedEventArgs e) => ShowSearch(SearchTab.References);

    public SearchWindow ShowSearch(SearchTab tab)
    {
        if (_search is not { IsVisible: true })
        {
            _search = new SearchWindow(Vm);
            _search.Show(this);
        }
        _search.FocusTab(tab);
        return _search;
    }

    private void OnReferencesReady(PackageReferences references)
    {
        if (_search is { IsVisible: true } search) search.ShowReferences(references);
        ShowSearch(SearchTab.References);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Vm.Settings);
        if (await dialog.ShowDialog<bool>(this))
        {
            Vm.SaveSettings();
            Vm.ApplyReadOptions();
            ShowHotkeysInMenus();
            if (dialog.RequiresReload) await Vm.InitializeGameAsync(dialog.AesEndpointChanged); // also loads the localization
            else if (dialog.LanguageChanged) await Vm.LoadLocalizationAsync();
        }
    }

    private ModelViewerWindow? _viewer;

    private void OnModelViewerClick(object? sender, RoutedEventArgs e) => ShowViewer();

    private void OnModelReady(ViewerModel model) => ShowViewer().AddModel(model);

    private void OnMapReady(SceneItem[] map) => ShowViewer().ShowMap(map);

    private void OnMaterialReady(ViewerMaterial material, ViewerModel cube) => ShowViewer().ShowMaterial(material, cube);

    private ModelViewerWindow ShowViewer()
    {
        if (_viewer is not { IsVisible: true })
        {
            _viewer = new ModelViewerWindow { Settings = Vm.Settings, AnimationSource = Vm };
            _viewer.Show();
        }
        _viewer.Activate();
        return _viewer;
    }

    // ---------- Audio player ----------

    private AudioPlayerViewModel? _audio;
    private AudioPlayerWindow? _audioPlayer;

    /// <summary>The open Audio Player window, if any (for the test harness).</summary>
    public AudioPlayerWindow? AudioPlayerView => _audioPlayer is { IsVisible: true } window ? window : null;

    // ---------- Packages › Favorite Directories (FModel's custom directories) ----------

    private void OnFavoritesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => BuildFavoritesMenu();

    // FModel's layout: "Add Directory", a separator, then each favorite with Go To / Edit / Delete
    private void BuildFavoritesMenu()
    {
        static MenuItem Item(string header, string icon, EventHandler<RoutedEventArgs> click)
        {
            var item = new MenuItem { Header = header, Icon = new Controls.Glyph { Data = (Avalonia.Media.Geometry) Application.Current!.FindResource(icon)! } };
            item.Click += click;
            return item;
        }

        var items = new List<Control> { Item("Add Directory…", "DirectoriesAddIcon", async (_, _) => await EditFavoriteAsync(null)) };
        if (Vm.Favorites.Count > 0) items.Add(new Separator());
        foreach (var favorite in Vm.Favorites)
        {
            var entry = new MenuItem { Header = favorite.Header.Replace("_", "__") }; // '_' marks an access key
            ToolTip.SetTip(entry, favorite.Path);
            entry.ItemsSource = new[]
            {
                Item("Go To", "GoToIcon", async (_, _) => await Vm.GoToFolderAsync(favorite.Path)),
                Item("Edit Directory…", "EditIcon", async (_, _) => await EditFavoriteAsync(favorite)),
                Item("Delete Directory", "RemoveIcon", (_, _) => Vm.RemoveFavorite(favorite))
            };
            items.Add(entry);
        }
        FavoritesMenu.ItemsSource = items;
    }

    private async Task EditFavoriteAsync(FavoriteDirectory? favorite)
    {
        // A new favorite starts from the selected folder, if any
        var start = favorite ?? (Vm.SelectedFolder is { } folder ? new FavoriteDirectory(folder.Name, folder.FullPath) : new FavoriteDirectory());
        var dialog = new FavoriteDirectoryWindow(start, Vm.FolderExists);
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is not { } result) return;
        if (favorite is null) Vm.AddFavorite(result);
        else Vm.ReplaceFavorite(favorite, result);
    }

    private void OnFavoriteFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFolder is { } folder) Vm.AddFavorite(new FavoriteDirectory(folder.Name, folder.FullPath));
    }

    // ---------- Favorite Directories tab ----------

    private async void OnGoToFavoriteClick(object? sender, RoutedEventArgs e) => await GoToSelectedFavoriteAsync();

    private async void OnFavoriteDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        await GoToSelectedFavoriteAsync();
    }

    private async void OnFavoritesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await GoToSelectedFavoriteAsync();
        }
        else if (e.Key is Key.Delete or Key.Back && Vm.SelectedFavorite is { } favorite)
        {
            e.Handled = true;
            Vm.RemoveFavorite(favorite);
        }
    }

    // Opens the folder in the Folders tab (loading every archive first when nothing is loaded yet)
    private async Task GoToSelectedFavoriteAsync()
    {
        if (Vm.SelectedFavorite is not { } favorite || !Vm.IsReady) return;
        if (await Vm.GoToFolderAsync(favorite.Path) && Vm.SelectedFolder is { } folder)
            Dispatcher.UIThread.Post(() => FolderList.ScrollIntoView(folder), DispatcherPriority.Background);
    }

    private async void OnAddFavoriteClick(object? sender, RoutedEventArgs e) => await EditFavoriteAsync(null);

    private async void OnEditFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFavorite is { } favorite) await EditFavoriteAsync(favorite);
    }

    private void OnMoveFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFavorite is { } favorite && sender is Control { Tag: string offset }) Vm.MoveFavorite(favorite, int.Parse(offset));
    }

    private void OnDeleteFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFavorite is { } favorite) Vm.RemoveFavorite(favorite);
    }

    private async void OnCopyFavoritePathClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFavorite is { } favorite && Clipboard is { } clipboard) await clipboard.SetTextAsync(favorite.Path);
    }

    private async void OnExportFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<FolderExport>(tag, out var kind) && Vm.SelectedFolder is { } folder)
            await Vm.ExportFolderAsync(folder, kind);
    }

    private async void OnCopyFolderPathClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFolder is { } folder && Clipboard is { } clipboard) await clipboard.SetTextAsync(folder.FullPath);
    }

    private ImageMergerWindow? _imageMerger;

    private void OnImageMergerClick(object? sender, RoutedEventArgs e) => ShowImageMerger();

    public ImageMergerWindow ShowImageMerger()
    {
        if (_imageMerger is not { IsVisible: true })
        {
            _imageMerger = new ImageMergerWindow(Vm);
            _imageMerger.Show(this);
        }
        _imageMerger.Activate();
        return _imageMerger;
    }

    private void OnMergerImageAdded() => ShowImageMerger();

    private ExportSessionWindow? _exportSession;

    private void OnExportSessionClick(object? sender, RoutedEventArgs e) => ShowExportSession();

    public ExportSessionWindow ShowExportSession()
    {
        if (_exportSession is not { IsVisible: true })
        {
            _exportSession = new ExportSessionWindow(Vm);
            _exportSession.Show(this);
        }
        _exportSession.Activate();
        return _exportSession;
    }

    private void OnAudioPlayerClick(object? sender, RoutedEventArgs e) => ShowAudioPlayer();

    private void OnSoundsReady(IReadOnlyList<ExtractedSound> sounds) => ShowAudioPlayer().Add(sounds, autoplay: true);

    /// <summary>The player keeps its playlist while the app runs, even if its window is closed.</summary>
    public AudioPlayerViewModel ShowAudioPlayer()
    {
        if (_audio is null)
        {
            _audio = new AudioPlayerViewModel(Vm.Settings);
            _audio.Message += (kind, message) => Vm.Log(kind, message);
        }
        if (_audioPlayer is not { IsVisible: true })
        {
            _audioPlayer = new AudioPlayerWindow(_audio);
            _audioPlayer.Closed += (_, _) => Vm.SaveSettings();
            _audioPlayer.Show();
        }
        _audioPlayer.Activate();
        return _audio;
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e) =>
        await new AboutWindow(Vm.Version).ShowDialog(this);

    // ---------- Archives ----------

    private async void OnArchiveDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm.LoadCommand.CanExecute(ArchivesList.SelectedItems))
            await Vm.LoadCommand.ExecuteAsync(ArchivesList.SelectedItems);
    }

    // ---------- Folders ----------

    private void OnLocateFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedFolder is { } folder) FolderList.ScrollIntoView(folder);
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedFolder();

    // Windows tree navigation, as in FModel: Right expands then enters, Left collapses then goes up
    private void OnFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm.SelectedFolder is not { } folder || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelectedFolder();
                break;
            case Key.Right when folder.HasSubFolders && !folder.IsExpanded:
                Vm.Folders.Expand(folder);
                break;
            case Key.Right when folder.IsExpanded:
                SelectFolder(Vm.Folders.FirstChild(folder));
                break;
            case Key.Left when folder.IsExpanded:
                Vm.Folders.Collapse(folder);
                break;
            case Key.Left:
                SelectFolder(Vm.Folders.Parent(folder));
                break;
            case Key.Right:
                break; // leaf: nothing to open, but keep the list from scrolling sideways
            default:
                return;
        }
        e.Handled = true;
    }

    private void SelectFolder(FolderNode? node)
    {
        if (node is null) return;
        Vm.Folders.SelectedFolder = node;
        FolderList.ScrollIntoView(node);
        (FolderList.ContainerFromItem(node) as InputElement)?.Focus(NavigationMethod.Directional);
    }

    // FModel's OpenAssets: jump to the Packages tab, or expand when the folder only holds folders
    private void OpenSelectedFolder()
    {
        if (Vm.SelectedFolder is not { } folder) return;
        if (folder.HasPackages) Vm.SelectedLeftTab = MainViewModel.PackagesTab;
        else Vm.Folders.Toggle(folder);
    }

    // ---------- Image pager ----------

    private void OnPreviousImageClick(object? sender, RoutedEventArgs e) => Vm.SelectedTab?.StepImage(-1);

    private void OnNextImageClick(object? sender, RoutedEventArgs e) => Vm.SelectedTab?.StepImage(1);

    // A click on the image gives it the keyboard, so ← → page through the images (FModel does it window-wide, but
    // here the folder tree and the text editor already use the arrows)
    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e) => ImagePanel.Focus();

    private void OnImageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right)) return;
        Vm.SelectedTab?.StepImage(e.Key == Key.Left ? -1 : 1);
        e.Handled = true;
    }

    // ---------- Text search (⌘F, like FModel's Ctrl+F / F3) ----------

    // The editor's own search panel (AvaloniaEdit): ⌘F opens it from anywhere in the window, ⌘G / F3 find the next
    // match and ⇧⌘G / ⇧F3 the previous one
    private bool HandleTextSearch(KeyEventArgs e)
    {
        var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var find = e.Key == Key.F && meta && !shift;
        var next = e.Key == Key.G && meta || e.Key == Key.F3 && !meta;
        if (!find && !next) return false;
        // FModel's Ctrl+F with the explorer open: its search box
        if (find && Vm.IsExplorerVisible)
        {
            ExplorerView.FocusSearch();
            return true;
        }
        if (Vm.IsExplorerVisible || Vm.SelectedTab?.Document is not { TextLength: > 0 }) return false;

        var panel = Editor.SearchPanel;
        if (find || panel.IsClosed)
        {
            // Start from what is selected, like most editors
            if (Editor.SelectionLength is > 0 and < 200 && !Editor.SelectedText.Contains('\n')) panel.SearchPattern = Editor.SelectedText;
            panel.Open();
            panel.Reactivate();
            if (find) return true;
        }
        if (shift) panel.FindPrevious();
        else panel.FindNext();
        return true;
    }

    // ---------- Hotkeys (Settings › Hotkeys) ----------

    // FModel's OnWindowKeyDown: tab switching and exports. Not while typing in a text field.
    private async void OnHotkeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled) return;
        if (HandleTextSearch(e))
        {
            e.Handled = true;
            return;
        }
        // FModel's BrowseBack (⌘[ here): from the tabs back to the explorer, else Packages → Folders → parent folder
        if (e.Key == Key.OemOpenBrackets && e.KeyModifiers == KeyModifiers.Meta)
        {
            BrowseBack();
            e.Handled = true;
            return;
        }
        // FModel's Escape: stops a folder export or the Export Session
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _vm.CanCancel)
        {
            _vm.CancelOperation();
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox { IsReadOnly: false }) return;
        var hotkeys = _vm.Settings.Hotkeys;

        // FModel's Alt+0…9: the explorer's category filter
        if (_vm.IsExplorerVisible && e.KeyModifiers == KeyModifiers.Alt && e.Key is >= Key.D0 and <= Key.D9)
        {
            _vm.Explorer.SelectCategory(e.Key - Key.D0);
            e.Handled = true;
            return;
        }

        if (hotkeys.SwitchAssetExplorer.IsTriggered(e) && _vm.NewAssetExplorer) _vm.IsExplorerVisible = !_vm.IsExplorerVisible;
        else if (hotkeys.AssetAddTab.IsTriggered(e)) _vm.AddTabCommand.Execute(null);
        else if (hotkeys.AssetRemoveTab.IsTriggered(e)) _vm.CloseTabCommand.Execute(null);
        else if (hotkeys.AssetLeftTab.IsTriggered(e)) SwitchTab(-1);
        else if (hotkeys.AssetRightTab.IsTriggered(e)) SwitchTab(1);
        else if (hotkeys.DirLeftTab.IsTriggered(e)) _vm.SelectedLeftTab = Math.Max(0, _vm.SelectedLeftTab - 1);
        else if (hotkeys.DirRightTab.IsTriggered(e)) _vm.SelectedLeftTab = Math.Min(MainViewModel.PackagesTab, _vm.SelectedLeftTab + 1);
        else if (ExportHotkey(hotkeys, e) is { } export)
        {
            e.Handled = true;
            // FModel exports what's selected in the focused list: the folder tree's folder, the explorer's tiles (its
            // folders whole), or the packages
            if (!_vm.IsReady) return;
            var kind = FolderExportOf(export);
            var focused = FocusManager?.GetFocusedElement() as Visual;
            if (focused is not null && (focused == FolderList || focused.FindAncestorOfType<ListBox>() == FolderList) && _vm.SelectedFolder is { } folder)
            {
                await _vm.ExportFolderAsync(folder, kind);
            }
            else if (_vm.IsExplorerVisible && _vm.Explorer.SelectedEntries.Any())
            {
                if (_vm.Explorer.SelectedFiles.Select(f => f.Item).ToArray() is { Length: > 0 } tiles) await export.ExecuteAsync(tiles);
                foreach (var tile in _vm.Explorer.SelectedEntries.OfType<ExplorerFolder>().ToList()) await _vm.ExportFolderAsync(tile.Node, kind);
            }
            else if (_vm.SelectedLeftTab == MainViewModel.PackagesTab && PackagesList.SelectedItems is { Count: > 0 } selected)
                await export.ExecuteAsync(selected.Cast<object>().ToArray());
            return;
        }
        else return;
        e.Handled = true;
    }

    private void BrowseBack()
    {
        if (_vm is not { } vm) return;
        if (vm.NewAssetExplorer && !vm.IsExplorerVisible)
        {
            vm.IsExplorerVisible = true;
            return;
        }
        if (vm.SelectedLeftTab == MainViewModel.PackagesTab) vm.SelectedLeftTab = MainViewModel.FoldersTab;
        else if (vm.SelectedLeftTab == MainViewModel.FoldersTab && vm.SelectedFolder is { } folder && vm.Folders.Parent(folder) is { } parent) SelectFolder(parent);
    }

    // The mouse's back button, like FModel's BrowseBack
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsXButton1Pressed) return;
        BrowseBack();
        e.Handled = true;
    }

    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<System.Collections.IList?>? ExportHotkey(HotkeySettings hotkeys, KeyEventArgs e)
    {
        var vm = Vm;
        if (hotkeys.ExportData.IsTriggered(e)) return vm.ExportRawDataCommand;
        if (hotkeys.ExportProperties.IsTriggered(e)) return vm.ExportPropertiesCommand;
        if (hotkeys.ExportTextures.IsTriggered(e)) return vm.ExportTextureCommand;
        if (hotkeys.ExportModels.IsTriggered(e)) return vm.ExportModelCommand;
        if (hotkeys.ExportWorlds.IsTriggered(e)) return vm.ExportWorldCommand;
        if (hotkeys.ExportAnimations.IsTriggered(e)) return vm.ExportAnimationCommand;
        if (hotkeys.ExportAudio.IsTriggered(e)) return vm.ExportAudioCommand;
        if (hotkeys.ExportCode.IsTriggered(e)) return vm.ExportCodeCommand;
        return null;
    }

    private FolderExport FolderExportOf(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<System.Collections.IList?> command)
    {
        var vm = Vm;
        if (command == vm.ExportRawDataCommand) return FolderExport.RawData;
        if (command == vm.ExportPropertiesCommand) return FolderExport.Properties;
        if (command == vm.ExportTextureCommand) return FolderExport.Textures;
        if (command == vm.ExportModelCommand) return FolderExport.Models;
        if (command == vm.ExportWorldCommand) return FolderExport.Worlds;
        if (command == vm.ExportAnimationCommand) return FolderExport.Animations;
        if (command == vm.ExportCodeCommand) return FolderExport.Code;
        return FolderExport.Audio;
    }

    private void SwitchTab(int step)
    {
        var index = Vm.SelectedTab is { } tab ? Vm.Tabs.IndexOf(tab) : 0;
        var next = Math.Clamp(index + step, 0, Vm.Tabs.Count - 1);
        if (next != index) Vm.SelectedTab = Vm.Tabs[next];
    }

    // FModel shows each export's hotkey in the menu (InputGestureText)
    private void ShowHotkeysInMenus()
    {
        if (_vm is null) return;
        var hotkeys = _vm.Settings.Hotkeys;
        static KeyGesture? Gesture(Hotkey hotkey) => hotkey.IsAssigned ? new KeyGesture(hotkey.Key, hotkey.Modifiers) : null;
        ExportDataItem.InputGesture = Gesture(hotkeys.ExportData);
        ExportPropertiesItem.InputGesture = Gesture(hotkeys.ExportProperties);
        ExportTexturesItem.InputGesture = Gesture(hotkeys.ExportTextures);
        ExportModelsItem.InputGesture = Gesture(hotkeys.ExportModels);
        ExportWorldsItem.InputGesture = Gesture(hotkeys.ExportWorlds);
        ExportAnimationsItem.InputGesture = Gesture(hotkeys.ExportAnimations);
        ExportAudioItem.InputGesture = Gesture(hotkeys.ExportAudio);
        ExportCodeItem.InputGesture = Gesture(hotkeys.ExportCode);
        FolderExportDataItem.InputGesture = Gesture(hotkeys.ExportData);
        FolderExportPropertiesItem.InputGesture = Gesture(hotkeys.ExportProperties);
        FolderExportTexturesItem.InputGesture = Gesture(hotkeys.ExportTextures);
        FolderExportCodeItem.InputGesture = Gesture(hotkeys.ExportCode);
        FolderExportModelsItem.InputGesture = Gesture(hotkeys.ExportModels);
        FolderExportWorldsItem.InputGesture = Gesture(hotkeys.ExportWorlds);
        FolderExportAnimationsItem.InputGesture = Gesture(hotkeys.ExportAnimations);
        FolderExportAudioItem.InputGesture = Gesture(hotkeys.ExportAudio);
        ToolTip.SetTip(ExplorerToggle, hotkeys.SwitchAssetExplorer.IsAssigned
            ? $"Switch Asset Explorer ({hotkeys.SwitchAssetExplorer})" : "Switch Asset Explorer");
    }

    // ---------- Packages ----------

    private async void OnPackageDoubleTapped(object? sender, TappedEventArgs e) => await Vm.ExtractSelectedCommand.ExecuteAsync(null);

    private async void OnPackageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await Vm.ExtractSelectedCommand.ExecuteAsync(null);
    }

    private async void OnExtractNewTabClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PackagesList.SelectedItems?.OfType<AssetItem>().ToArray() ?? [])
            await Vm.ExtractAsync(item.File, newTab: true);
    }

    private GameFile? SelectedPackageFile => (PackagesList.SelectedItem as AssetItem)?.File ?? Vm.SelectedPackage?.File;

    private async void OnShowMetadataClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PackagesList.SelectedItems?.OfType<AssetItem>().Where(i => i.IsPackage).ToArray() ?? [])
            await Vm.ShowMetadataAsync(item.File);
    }

    private async void OnFindReferencesClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedPackageFile is { } file) await Vm.FindReferencesAsync(file);
    }

    private async void OnDecompileClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PackagesList.SelectedItems?.OfType<AssetItem>().Where(i => i.IsPackage).ToArray() ?? [])
            await Vm.DecompileAsync(item.File);
    }

    // ---------- Tabs ----------

    private static AssetTab? MenuTab(object? sender) => (sender as MenuItem)?.DataContext as AssetTab;

    private void OnCloseTabClick(object? sender, RoutedEventArgs e) => Vm.CloseTabCommand.Execute(MenuTab(sender));

    private void OnCloseOtherTabsClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { } keep) return;
        foreach (var tab in Vm.Tabs.Where(t => t != keep).ToArray()) Vm.CloseTabCommand.Execute(tab);
    }

    private void OnCloseAllTabsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var tab in Vm.Tabs.ToArray()) Vm.CloseTabCommand.Execute(tab);
    }

    private async void OnTabShowMetadataClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTab(sender)?.Entry is { } file) await Vm.ShowMetadataAsync(file);
    }

    private async void OnTabFindReferencesClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTab(sender)?.Entry is { } file) await Vm.FindReferencesAsync(file);
    }

    // FModel's Save Properties / Save Decompiled: the tab's text as .json, or .cpp for a decompiled blueprint
    private async void OnTabSaveTextClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is not { Entry: { } file } tab || tab.Document.TextLength == 0) return;
        // Text files (ini, xml, lua…) keep their own extension
        var extension = tab.Header.EndsWith("(Decompiled)") ? "cpp" : file.IsUePackage ? "json" : file.Extension;
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Text",
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(file.Name)}.{extension}",
            DefaultExtension = extension
        });
        if (target is null) return;

        await using var stream = await target.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(tab.Document.Text);
        Vm.Log(LogKind.Information, $"Successfully saved '{target.Name}'");
    }

    private async void OnTabDecompileClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTab(sender)?.Entry is { } file) await Vm.DecompileAsync(file);
    }

    private void OnTabsHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Middle click closes a tab, like FModel
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        if ((e.Source as StyledElement)?.DataContext is AssetTab tab) Vm.CloseTabCommand.Execute(tab);
    }

    private async void OnSaveImageClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedTab is not { ImagePng: { } png } tab) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image",
            SuggestedFileName = $"{tab.ImageName ?? "image"}.png",
            DefaultExtension = "png",
            FileTypeChoices = [FilePickerFileTypes.ImagePng]
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        Vm.Log(LogKind.Information, $"Successfully saved '{file.Name}'");
    }
}
