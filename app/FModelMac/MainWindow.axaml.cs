using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CUE4Parse.FileProvider.Objects;
using FModelMac.Rendering;
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
    }

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
            _vm.SoundsReady -= OnSoundsReady;
            _vm.ReferencesReady -= OnReferencesReady;
            _vm.AnimationReady = null;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is null) return;

        _vm.SetClipboardText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Logs.CollectionChanged += OnLogsChanged;
        _vm.ModelReady += OnModelReady;
        _vm.MapReady += OnMapReady;
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
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetTab.HasImage)) UpdateImageColumn();
    }

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

    private async Task ShowDirectorySelectorAsync()
    {
        var dialog = new DirectorySelectorWindow(Vm.Settings);
        if (await dialog.ShowDialog<bool>(this))
        {
            Vm.SaveSettings();
            await Vm.InitializeGameAsync();
        }
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
            if (dialog.RequiresReload) await Vm.InitializeGameAsync(); // also loads the localization
            else if (dialog.LanguageChanged) await Vm.LoadLocalizationAsync();
        }
    }

    private ModelViewerWindow? _viewer;

    private void OnModelViewerClick(object? sender, RoutedEventArgs e) => ShowViewer();

    private void OnModelReady(ViewerModel model) => ShowViewer().AddModel(model);

    private void OnMapReady(SceneItem[] map) => ShowViewer().ShowMap(map);

    private ModelViewerWindow ShowViewer()
    {
        if (_viewer is not { IsVisible: true })
        {
            _viewer = new ModelViewerWindow();
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
        if (folder.HasPackages) Vm.SelectedLeftTab = 2;
        else Vm.Folders.Toggle(folder);
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
