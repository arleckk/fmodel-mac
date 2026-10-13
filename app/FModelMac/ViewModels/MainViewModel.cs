using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse_Conversion;
using CUE4Parse.Utils;
using CUE4Parse.UE4.Assets.Exports.Material;
using FModelMac.Rendering;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModelMac.Services;
using FModelMac.Services.Audio;
using Newtonsoft.Json;
using Serilog;

namespace FModelMac.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAnimationSource
{
    private const int MaxTabs = 20;

    public const string DonateLink = "https://fmodel.app/donate";
    public const string ReleasesLink = "https://github.com/4sval/FModel/releases";
    public const string IssueLink = "https://github.com/4sval/FModel/discussions/categories/q-a";
    public const string DiscordLink = "https://fmodel.app/discord";

    private GameArchive? _archive;
    private SoundExtractor? _sounds;
    private AnimationLibrary? _animations;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<PcmAudio?>> _notifySounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<ViewerEffect?>> _notifyEffects = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Settings { get; } = AppSettings.Load();
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Set by the window: copies text to the system clipboard.</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    public GameArchive? Archive => _archive;
    public FolderTreeViewModel Folders { get; } = new();
    /// <summary>FModel's new asset explorer, shown in place of the asset tabs.</summary>
    public ExplorerViewModel Explorer { get; }
    public ObservableCollection<ArchiveItem> Archives { get; } = [];
    public ObservableCollection<AssetItem> Packages { get; } = [];
    public ObservableCollection<AssetTab> Tabs { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    // FModel's loading modes; the last two compare the game with an older backup (Directory › Backup)
    public const string LoadNew = "All (New)", LoadModified = "All (Modified)";
    public IReadOnlyList<string> LoadingModes { get; } = ["Multiple", "All", LoadNew, LoadModified];

    /// <summary>Asks for a backup file for "All (New)"/"All (Modified)"; set by the window (a file picker).</summary>
    public Func<Task<string?>>? PickBackupFile { get; set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private StatusKind _statusKind = StatusKind.Ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _statusLabel = "Ready";
    [ObservableProperty] private string _statusExtra = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _loadingMode;
    [ObservableProperty] private ArchiveItem? _selectedArchive;
    [ObservableProperty] private int _selectedLeftTab;
    [ObservableProperty] private string _packageSearchText = "";
    [ObservableProperty] private AssetItem? _selectedPackage;

    /// <summary>The asset explorer in place of the asset tabs (FModel's IsAssetsExplorerVisible).</summary>
    [ObservableProperty] private bool _isExplorerVisible;

    /// <summary>FModel's "Preview New Explorer System": off hides the explorer and its toggle.</summary>
    public bool NewAssetExplorer
    {
        get => Settings.NewAssetExplorer;
        set
        {
            if (value == Settings.NewAssetExplorer) return;
            Settings.NewAssetExplorer = value;
            SaveSettings();
            OnPropertyChanged();
            if (!value) IsExplorerVisible = false;
        }
    }

    partial void OnIsExplorerVisibleChanged(bool value)
    {
        if (value && !Settings.NewAssetExplorer) IsExplorerVisible = false;
    }
    [ObservableProperty] private AssetTab? _selectedTab;
    [ObservableProperty] private bool _isLoggerExpanded;

    public bool IsReady => !IsBusy;
    public string StatusText => StatusKind == StatusKind.Loading ? $"{StatusLabel} …" : StatusLabel;
    public bool IsMultipleMode => LoadingMode == "Multiple";
    public FolderNode? SelectedFolder => Folders.SelectedFolder;
    public string PackagesHeader => $"{SelectedFolder?.PackageCount ?? 0} Packages";
    public string FolderSummary => SelectedFolder is { } f
        ? $"'{f.Name}' has {f.FolderCount} folders and {f.PackageCount} packages"
        : "'None' has 0 folders and 0 packages";

    public MainViewModel()
    {
        _loadingMode = LoadingModes.Contains(Settings.LoadingMode) ? Settings.LoadingMode : "All";
        _isLoggerExpanded = Settings.IsLoggerExpanded;
        Folders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderTreeViewModel.SelectedFolder)) OnSelectedFolderChanged();
        };
        Explorer = new ExplorerViewModel(Settings, () => _archive?.Provider);
        Explorer.SelectionChanged += entry =>
        {
            if (entry is ExplorerFile file) SelectedPackage = Packages.FirstOrDefault(p => p.File == file.File) ?? file.Item;
        };
        Tabs.Add(new AssetTab());
        SelectedTab = Tabs[0];
        UpdateTitle();
    }

    public bool HasGameDirectory => !string.IsNullOrWhiteSpace(Settings.ArchiveDir) && Directory.Exists(Settings.ArchiveDir);

    // ---------- Status & logs ----------

    public void SetStatus(StatusKind kind, string label)
    {
        StatusKind = kind;
        StatusLabel = label;
    }

    public void Log(LogKind kind, string message)
    {
        Logs.Add(new LogEntry(kind, message));
        switch (kind)
        {
            case LogKind.Error: Serilog.Log.Error("{Message}", message); break;
            case LogKind.Warning: Serilog.Log.Warning("{Message}", message); break;
            default: Serilog.Log.Information("{Message}", message); break;
        }
    }

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    partial void OnIsLoggerExpandedChanged(bool value)
    {
        Settings.IsLoggerExpanded = value;
        SaveSettings();
    }

    partial void OnLoadingModeChanged(string value)
    {
        Settings.LoadingMode = value;
        SaveSettings();
        OnPropertyChanged(nameof(IsMultipleMode));
    }

    // ---------- Game initialization (Directory Selector / AES) ----------

    /// <summary>Mounts the configured game and fills the Archives tab, like FModel does at startup.</summary>
    /// <param name="refreshAes">Ask the AES endpoint whatever "AES Reload at Launch" says (its settings changed).</param>
    public async Task InitializeGameAsync(bool refreshAes = false)
    {
        if (!HasGameDirectory)
        {
            Log(LogKind.Warning, "No game directory configured, use Directory > Selector");
            return;
        }
        if (!Enum.TryParse<EGame>(Settings.Game, true, out var game))
        {
            Log(LogKind.Error, $"Unknown UE version '{Settings.Game}'");
            return;
        }

        IsBusy = true;
        SetStatus(StatusKind.Loading, "Loading game archives");
        ResetGame();
        var remount = false;

        try
        {
            if (refreshAes || ShouldReloadAes()) await RefreshAesAsync();
            var usmap = await ResolveMappingsAsync();
            var versioning = Settings.CurrentVersioning;
            var options = new LoadOptions(Settings.ArchiveDir!, game, Settings.AesKey, Settings.DynamicKeys, usmap, versioning);
            var progress = new Progress<string>(label => StatusLabel = label);
            _archive = await Task.Run(() => GameArchive.Load(options, progress));
            ApplyReadOptions();
            _sounds = new SoundExtractor(_archive.Provider, Settings.ArchiveDir!);
            _animations = new AnimationLibrary(_archive);

            foreach (var archive in _archive.Archives)
                Archives.Add(new ArchiveItem(archive));

            var total = _archive.Archives.Count;
            Log(LogKind.Information, $"{_archive.ProjectName} ({game}) | Mounted: {_archive.MountedCount}/{total} | " +
                                     $"Files: x{_archive.Provider.Files.Count:N0} | {_archive.LoadTime.TotalSeconds:F2}s");
            if (_archive.VirtualPathCount > 0) Log(LogKind.Information, $"{_archive.VirtualPathCount} virtual paths loaded");
            else Log(LogKind.Warning, "Could not load virtual paths, plugin manifest may not exist");
            if (_archive.LockedCount > 0)
                Log(LogKind.Warning, $"{_archive.LockedCount} archive(s) need an AES key, see Directory > AES");
            if (total == 0)
                Log(LogKind.Warning, $"No archives found in '{Settings.ArchiveDir}'");
            if (versioning.PlatformValue != ETexturePlatform.DesktopMobile || versioning.HasOverrides)
                Log(LogKind.Information, $"Versioning: {versioning.PlatformValue} textures" +
                                         $"{(versioning.CustomVersions is { Count: > 0 } cv ? $", {cv.Count} custom version(s)" : "")}" +
                                         $"{(versioning.Options is { Count: > 0 } o ? $", {o.Count} option override(s)" : "")}" +
                                         $"{(versioning.MapStructTypes is { Count: > 0 } m ? $", {m.Count} map struct type override(s)" : "")}");
            await LoadLocalizationAsync();

            StatusExtra = $"{game} · {_archive.MountedCount}/{total} archives";
            UpdateTitle();
            SetStatus(StatusKind.Ready, "Ready");
            LoadFavorites(_archive.ProjectName);
            remount = ApplyEndpointPreset(_archive.ProjectName);
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to initialize {Dir}", Settings.ArchiveDir);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
        finally
        {
            IsBusy = false;
        }
        if (remount) await InitializeGameAsync(refreshAes: true);
    }

    // FModel's "AES Reload at Launch": Always, Never or Once Per Day (per game directory)
    private bool ShouldReloadAes()
    {
        if (Settings.AesEndpoint is not { IsUsable: true }) return false;
        switch (Settings.AesReload)
        {
            case AesReload.Always:
                return true;
            case AesReload.OncePerDay:
                var key = Settings.ArchiveDir ?? "";
                if (Settings.LastAesReload.TryGetValue(key, out var last) && last.Date == DateTime.Today) return false;
                Settings.LastAesReload[key] = DateTime.Today;
                return true;
            default:
                return false;
        }
    }

    // FModel configures the uedb.dev endpoints for Fortnite and VALORANT; do the same once per game directory
    private bool ApplyEndpointPreset(string projectName)
    {
        var name = projectName switch
        {
            "FortniteGame" => "Fortnite",
            "ShooterGame" => "VALORANT",
            _ => null
        };
        if (name is null || Settings.AesEndpoint is not null || Settings.MappingEndpoint is not null ||
            !Settings.EndpointPresetsApplied.Add(Settings.ArchiveDir!)) return false;
        var preset = Endpoints.Presets.First(p => p.Name == name);
        Settings.AesEndpoint = preset.Aes.Clone();
        Settings.MappingEndpoint = preset.Mapping.Clone();
        SaveSettings();
        Log(LogKind.Information, $"{name} detected: using FModel's {name} endpoints for AES keys and mappings (Settings › Endpoints)");
        return true;
    }

    /// <summary>FModel's LoadLocalizedResources: the localized texts packages show, in Settings › Packages Language.</summary>
    public async Task LoadLocalizationAsync()
    {
        if (_archive is not { } archive) return;
        var language = Settings.AssetLanguageValue;
        StatusLabel = "Loading localized resources";
        try
        {
            var result = await Task.Run(() => archive.LoadLocalization(language));
            var name = language.GetDescription();
            if (result.Available.Length == 0)
                Log(LogKind.Information, "This game has no localized resources (.locres)");
            else if (!result.Loaded || result.Count == 0)
            {
                var others = result.Available.Where(c => !c.Equals(result.Culture, StringComparison.OrdinalIgnoreCase)).ToArray();
                Log(LogKind.Warning, $"No localized texts for '{name}' ({result.Culture})" +
                                     (others.Length > 0 ? $"; this game has {string.Join(", ", others)}" : " outside the engine's"));
            }
            else
                Log(LogKind.Information, $"{result.Count:N0} localized resources loaded for '{name}' ({result.Culture})");
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not load localization for {Language}", language);
            Log(LogKind.Warning, $"Could not load localized resources: {e.Message}");
        }
    }

    /// <summary>FModel's RefreshAes: the AES endpoint replaces the saved keys when it answers with valid ones.</summary>
    private async Task RefreshAesAsync()
    {
        if (Settings.AesEndpoint is not { IsUsable: true } endpoint) return;
        StatusLabel = "Refreshing AES keys";
        try
        {
            var aes = await Endpoints.GetAesAsync(endpoint);
            if (!aes.IsValid)
            {
                Log(LogKind.Warning, $"The AES endpoint returned no usable key, keeping the saved ones ({endpoint.Url})");
                return;
            }
            if (aes.MainKey.Length == 66) Settings.AesKey = aes.MainKey;
            Settings.DynamicKeys = aes.DynamicKeys.GroupBy(k => k.Guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
            SaveSettings();
            Log(LogKind.Information, $"AES keys refreshed from the endpoint: {(aes.MainKey.Length == 66 ? "main key" : "no main key")}" +
                                     $" + {aes.DynamicKeys.Count} dynamic key(s)");
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "AES endpoint {Url} failed", endpoint.Url);
            Log(LogKind.Warning, $"Could not refresh AES keys from {endpoint.Url}: {e.Message}");
        }
    }

    /// <summary>FModel's InitMappings: a local mapping file first, then the mapping endpoint (cached).</summary>
    private async Task<string?> ResolveMappingsAsync(bool force = false)
    {
        if (!string.IsNullOrWhiteSpace(Settings.UsmapPath))
        {
            Log(LogKind.Information, $"Mappings pulled from '{Path.GetFileName(Settings.UsmapPath)}'");
            return Settings.UsmapPath;
        }
        if (Settings.MappingEndpoint is not { IsUsable: true } endpoint) return null;
        StatusLabel = "Fetching mappings";
        var (path, source) = await Endpoints.ResolveMappingAsync(endpoint, force);
        if (path is null) Log(LogKind.Warning, $"Mappings: {source}");
        else Log(source.StartsWith("endpoint unavailable") ? LogKind.Warning : LogKind.Information, $"Mappings pulled from the endpoint: {source}");
        return path;
    }

    /// <summary>Downloads the mapping again and remounts the game (Settings › Endpoints › Reload).</summary>
    public async Task ReloadMappingsAsync()
    {
        if (Settings.MappingEndpoint is { IsUsable: true } && string.IsNullOrWhiteSpace(Settings.UsmapPath))
            await ResolveMappingsAsync(force: true);
        await InitializeGameAsync();
    }

    private void ResetGame()
    {
        Favorites.Clear();
        ExportSession.Clear();
        References = null;
        Archives.Clear();
        Packages.Clear();
        Folders.Clear();
        OnSelectedFolderChanged();
        _archive?.Dispose();
        _archive = null;
        _animations = null;
        _notifySounds.Clear();
        _notifyEffects.Clear();
        StatusExtra = "";
    }

    // ---------- Favorite Directories (FModel's custom directories) ----------

    /// <summary>The current game's favorite directories, as shown in Packages › Favorite Directories.</summary>
    public ObservableCollection<FavoriteDirectory> Favorites { get; } = [];

    // A game directory gets FModel's presets the first time it's mounted, then the list is the user's
    private void LoadFavorites(string projectName)
    {
        var key = Settings.ArchiveDir ?? "";
        if (!Settings.FavoriteDirectories.TryGetValue(key, out var list))
        {
            Settings.FavoriteDirectories[key] = list = FavoriteDirectory.Presets(projectName);
            SaveSettings();
        }
        Favorites.Clear();
        foreach (var favorite in list) Favorites.Add(favorite);
    }

    private void SaveFavorites()
    {
        Settings.FavoriteDirectories[Settings.ArchiveDir ?? ""] = Favorites.ToList();
        SaveSettings();
    }

    public void AddFavorite(FavoriteDirectory favorite)
    {
        Favorites.Add(favorite);
        SaveFavorites();
        Log(LogKind.Information, $"Added '{favorite.Header}' to Favorite Directories");
    }

    public void ReplaceFavorite(FavoriteDirectory old, FavoriteDirectory updated)
    {
        var index = Favorites.IndexOf(old);
        if (index < 0) return;
        Favorites[index] = updated;
        SaveFavorites();
    }

    public void RemoveFavorite(FavoriteDirectory favorite)
    {
        if (!Favorites.Remove(favorite)) return;
        SaveFavorites();
    }

    /// <summary>Whether the mounted game has files under this folder.</summary>
    public bool FolderExists(string path)
    {
        if (_archive is null) return false;
        var prefix = FavoriteDirectory.Normalize(path) + "/";
        return _archive.Provider.Files.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>FModel's GoToCommand: shows a folder in the Folders tab, loading every archive first if needed.</summary>
    public async Task<bool> GoToFolderAsync(string path)
    {
        if (_archive is null) return false;
        if (Folders.VisibleFolders.Count == 0) await Load(null);
        path = FavoriteDirectory.Normalize(path);
        if (NavigateToFolder(path) is null)
        {
            Log(LogKind.Warning, $"There is no folder '{path}' in the loaded archives");
            return false;
        }
        SelectedLeftTab = 1;
        return true;
    }

    public void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception e) { Serilog.Log.Warning(e, "Could not save settings"); }
    }

    private void UpdateTitle()
    {
        var profile = Settings.CurrentProfile;
        var project = _archive?.ProjectName is { Length: > 0 } mounted ? mounted : null;
        var name = profile is null ? project
            : project is null || project.Equals(profile, StringComparison.OrdinalIgnoreCase) ? profile : $"{profile} ({project})";
        Title = name is null ? $"FModelMac ({Version})" : $"FModelMac ({Version}) - {name}";
    }

    /// <summary>Directory › Profiles: saves the current game and mounts another saved one, as it was left.</summary>
    public async Task SwitchProfileAsync(GameProfile profile)
    {
        if (IsBusy || profile == Settings.ActiveProfile) return;
        if (profile.ArchiveDir is not { } dir || !Directory.Exists(dir))
        {
            Log(LogKind.Error, $"Profile '{profile.Name}': the directory '{profile.ArchiveDir}' doesn't exist anymore (Directory › Selector to fix it)");
            return;
        }
        Settings.ApplyProfile(profile);
        SaveSettings();
        Log(LogKind.Information, $"Switched to profile '{profile.Name}' ({profile.Summary})");
        await InitializeGameAsync();
    }

    // ---------- Archives tab: Loading Mode + Load ----------

    private bool CanLoad() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task Load(IList? selectedItems)
    {
        if (_archive is null)
        {
            Log(LogKind.Warning, "Load a game first (Directory > Selector)");
            return;
        }

        HashSet<IVfsReader>? scope = null;
        if (IsMultipleMode)
        {
            scope = (selectedItems?.OfType<ArchiveItem>() ?? []).Where(a => a.IsEnabled).Select(a => (IVfsReader) a.Reader).ToHashSet();
            if (scope.Count == 0)
            {
                Log(LogKind.Warning, "Select at least one mounted archive to load in Multiple mode");
                return;
            }
        }

        if (LoadingMode is LoadNew or LoadModified)
        {
            if ((PickBackupFile is null ? null : await PickBackupFile()) is { } picked)
                await LoadBackupChangesAsync(picked, LoadingMode == LoadNew);
            return;
        }

        IsBusy = true;
        SetStatus(StatusKind.Loading, "Loading packages");
        try
        {
            var archive = _archive;
            var sw = Stopwatch.StartNew();
            var index = await Task.Run(() => archive.BuildIndex(scope));
            Folders.Populate(index);
            IsExplorerVisible = Settings.NewAssetExplorer; // FModel's LoadCommand shows the explorer
            Log(LogKind.Information, $"Loaded {index.FileCount:N0} packages from " +
                                     $"{scope?.Count ?? archive.MountedCount} archive(s) in {sw.Elapsed.TotalSeconds:F2}s");
            SelectedLeftTab = 1;
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Load failed");
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>FModel's "All (New)" / "All (Modified)": loads only what changed since an older backup.</summary>
    public async Task<BackupComparison?> LoadBackupChangesAsync(string backup, bool newFiles)
    {
        if (_archive is not { } archive || IsBusy) return null;
        IsBusy = true;
        SetStatus(StatusKind.Loading, "Comparing with the backup");
        try
        {
            var sw = Stopwatch.StartNew();
            Log(LogKind.Information, $"Backup file older than current game is '{Path.GetFileName(backup)}'");
            var (comparison, index) = await Task.Run(() =>
            {
                var compared = GameBackup.Compare(archive.Provider, backup);
                var wanted = newFiles ? compared.New : compared.Modified;
                return (compared, archive.BuildIndex(null, f => wanted.Contains(f.Path)));
            });
            Folders.Populate(index);
            IsExplorerVisible = Settings.NewAssetExplorer; // FModel's LoadCommand shows the explorer
            Log(LogKind.Information, $"Loaded {index.FileCount:N0} {(newFiles ? "new" : "modified")} packages in {sw.Elapsed.TotalSeconds:F2}s " +
                                     $"(backup of {comparison.BackupCount:N0} files: {comparison.New.Count:N0} new, " +
                                     $"{comparison.Modified.Count:N0} modified, {comparison.Removed:N0} removed)");
            SelectedLeftTab = 1;
            SetStatus(StatusKind.Completed, "Completed");
            return comparison;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not compare with {Backup}", backup);
            Log(LogKind.Error, $"Could not read '{Path.GetFileName(backup)}': {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>FModel's Directory › Backup › Create: the current game's file list in Output/Backups.</summary>
    public async Task<string?> CreateBackupAsync()
    {
        if (_archive is not { } archive) return null;
        SetStatus(StatusKind.Loading, "Creating backup");
        try
        {
            var (path, count) = await Task.Run(() => GameBackup.Create(archive.Provider, archive.ProjectName, GameBackup.Directory(Settings)));
            Log(LogKind.Information, $"Successfully created '{Path.GetFileName(path)}' ({count:N0} files, {AssetPreview.FormatSize(new FileInfo(path).Length)})");
            LastExportPath = path;
            SetStatus(StatusKind.Completed, "Completed");
            return path;
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not create a backup");
            Log(LogKind.Error, $"Could not create the backup: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
            return null;
        }
    }

    // ---------- Folders & Packages tabs ----------

    private void OnSelectedFolderChanged()
    {
        PackageSearchText = "";
        RefreshPackages();
        Explorer.ShowFolder(SelectedFolder);
        // FModel: picking a folder shows it in the explorer
        if (SelectedFolder is not null && Settings.NewAssetExplorer) IsExplorerVisible = true;
        OnPropertyChanged(nameof(SelectedFolder));
        OnPropertyChanged(nameof(PackagesHeader));
        OnPropertyChanged(nameof(FolderSummary));
    }

    partial void OnPackageSearchTextChanged(string value) => RefreshPackages();

    private void RefreshPackages()
    {
        Packages.Clear();
        if (SelectedFolder is not { } folder) return;

        var filter = PackageSearchText.Trim();
        foreach (var file in folder.Files)
        {
            if (filter.Length == 0 || file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Packages.Add(new AssetItem(file));
        }
    }

    [RelayCommand]
    private void ToggleFolder(FolderNode? node)
    {
        if (node is not null) Folders.Toggle(node);
    }

    [RelayCommand]
    private void CollapseAll() => Folders.CollapseAll();

    /// <summary>Selects a folder by path, expanding its ancestors (breadcrumb, search "Go To").</summary>
    public FolderNode? NavigateToFolder(string path)
    {
        var node = Folders.Reveal(path);
        if (node is not null) Folders.SelectedFolder = node;
        return node;
    }

    /// <summary>Shows a file in the Packages tab, loading every archive first if needed.</summary>
    public async Task GoToAsync(GameFile file)
    {
        if (Folders.VisibleFolders.Count == 0) await Load(null);
        if (NavigateToFolder(file.Directory) is null) return;
        SelectedLeftTab = 2;
        SelectedPackage = Packages.FirstOrDefault(p => p.File == file);
    }

    // ---------- Asset tabs ----------

    [RelayCommand]
    private void AddTab()
    {
        if (Tabs.Count >= MaxTabs) return;
        var tab = new AssetTab();
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(AssetTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;

        if (Tabs.Count == 1)
        {
            tab.Reset(null);
            return;
        }

        var index = Tabs.IndexOf(tab);
        tab.Reset(null);
        Tabs.Remove(tab);
        if (SelectedTab is null || SelectedTab == tab)
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    /// <summary>FModel's Extract: double-click reuses the current tab, the context menu can open a new one.</summary>
    public async Task ExtractAsync(GameFile file, bool newTab = false)
    {
        if (_archive is null) return;

        IsExplorerVisible = false;
        var tab = TargetTab(newTab);
        tab.Reset(file);
        var cts = tab.Loading = new CancellationTokenSource();
        var provider = _archive.Provider;
        SetStatus(StatusKind.Loading, $"Extracting {file.Name}");

        try
        {
            var sounds = _sounds;
            var lua = Settings.DecompileLua ? LuaOptions.From(Settings) : null;
            var platform = Settings.CurrentVersioning.PlatformValue;
            var result = await Task.Run(() => AssetPreview.Build(provider, file, cts.Token, sounds, lua, platform), cts.Token);
            var image = result.Images is [var first, ..] ? await Task.Run(() => new Bitmap(new MemoryStream(first.Png)), cts.Token) : null;
            if (cts.IsCancellationRequested) return;

            tab.Apply(result, image);
            SetStatus(StatusKind.Completed, "Completed");
            foreach (var warning in result.SoundWarnings ?? []) Log(LogKind.Warning, warning);
            if (result.Warning is { } previewWarning) Log(LogKind.Warning, previewWarning);
            if (result.Sounds is { Count: > 0 } found)
            {
                Log(LogKind.Information, found.Count == 1
                    ? $"Found sound '{found[0].FileName}' ({AssetPreview.FormatSize(found[0].Data.Length)})"
                    : $"Found {found.Count} sounds in '{file.Name}'");
                if (Settings.PlayAudioOnExtract) SoundsReady?.Invoke(found);
            }
            // Settings › Preview, per kind of asset
            var mesh = result.Mesh switch
            {
                UStaticMesh when Settings.PreviewStaticMeshes => result.Mesh,
                USkinnedAsset when Settings.PreviewSkeletalMeshes => result.Mesh,
                _ => null
            } ?? (Settings.SaveSkeletonAsMesh ? result.Skeleton : null);
            if (mesh is not null) await PreviewMeshAsync(mesh);
            if (Settings.PreviewAnimations && result.Animation is { } animation) await PreviewAnimationAsync(animation);
            if (Settings.PreviewWorlds && result.World is { } world) await PreviewMapAsync(world);
            if (Settings.PreviewMaterials && mesh is null && result.Material is { } material) await PreviewMaterialAsync(material);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            Serilog.Log.Error(e, "Failed to extract {Path}", file.Path);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    private AssetTab TargetTab(bool newTab)
    {
        if ((!newTab || Tabs.Count >= MaxTabs) && SelectedTab is not null) return SelectedTab;
        var tab = new AssetTab();
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>Applies Settings › General › Advanced to the mounted game, without mounting it again.</summary>
    public void ApplyReadOptions() => _archive?.ApplyReadOptions(Settings.ReadScriptData, Settings.ReadShaderMaps);

    /// <summary>FModel's Show Metadata: the package summary, names, imports and exports in a new tab.</summary>
    public Task ShowMetadataAsync(GameFile file) =>
        ShowTextAsync(file, "Metadata", "Json", $"Reading the metadata of {file.Name}", archive => archive.PackageMetadata(file));

    /// <summary>FModel's Decompile Blueprint: the package's classes as C++-like pseudocode in a new tab.</summary>
    public Task DecompileAsync(GameFile file) =>
        ShowTextAsync(file, "Decompiled", "Cpp", $"Decompiling {file.Name}", archive => archive.DecompileBlueprint(file),
            $"'{file.Name}' has no blueprint class to decompile");

    // Builds the text first and only then takes a tab, so a package with nothing to show doesn't leave an empty one
    private async Task ShowTextAsync(GameFile file, string kind, string highlight, string status, Func<GameArchive, string?> build,
        string? emptyMessage = null)
    {
        if (_archive is not { } archive || !file.IsUePackage) return;
        SetStatus(StatusKind.Loading, status);
        try
        {
            var text = await Task.Run(() => build(archive));
            if (text is null)
            {
                Log(LogKind.Warning, emptyMessage ?? $"Nothing to show for '{file.Name}'");
                SetStatus(StatusKind.Completed, "Completed");
                return;
            }
            IsExplorerVisible = false;
            var tab = TargetTab(newTab: SelectedTab?.Entry is not null);
            tab.Reset(file);
            tab.Header = $"{file.Name} ({kind})";
            tab.ApplyText(text, highlight);
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "{Kind} failed for {Path}", kind, file.Path);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    /// <summary>The last Find References result, shown in the References tab of the search window.</summary>
    public PackageReferences? References { get; private set; }

    /// <summary>Raised on the UI thread when Find References has a result.</summary>
    public event Action<PackageReferences>? ReferencesReady;

    /// <summary>FModel's Find References: the packages that import this one.</summary>
    public async Task FindReferencesAsync(GameFile file)
    {
        if (_archive is not { } archive || !file.IsUePackage) return;
        var indexing = archive.NeedsReferenceIndex;
        SetStatus(StatusKind.Loading, indexing ? "Indexing package imports" : $"Finding references to {file.Name}");
        try
        {
            var progress = new Progress<double>(p => StatusLabel = $"Indexing package imports ({p:P0})");
            var files = await Task.Run(() => archive.FindReferences(file, progress));
            if (indexing && archive.ReferenceIndexSummary is { } summary) Log(LogKind.Information, summary);
            References = new PackageReferences(file, files.ToArray());
            ReferencesReady?.Invoke(References);
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to find references to {Path}", file.Path);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    /// <summary>Raised on the UI thread with the sounds of an extracted package or file, for the audio player.</summary>
    public event Action<IReadOnlyList<ExtractedSound>>? SoundsReady;

    /// <summary>Raised on the UI thread with a mesh ready for the 3D viewer.</summary>
    public event Action<ViewerModel>? ModelReady;

    // Like FModel's Snooper: extracting a mesh also opens it in the 3D viewer
    public async Task PreviewMeshAsync(UObject mesh)
    {
        SetStatus(StatusKind.Loading, $"Loading {mesh.Name} in the 3D viewer");
        try
        {
            var context = ViewerContext();
            var model = await Task.Run(() => ViewerModel.FromExport(mesh, context));
            ModelReady?.Invoke(model);
            Log(LogKind.Information, model is { VertexCount: 0, Bones: { } bones }
                ? $"Loaded skeleton '{model.Name}' in the 3D viewer ({bones.Length:N0} bones)"
                : $"Loaded '{model.Name}' in the 3D viewer ({model.VertexCount:N0} vertices, " +
                  $"{model.TriangleCount:N0} triangles, {model.Sections.Length} materials)");
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not load {Mesh} in the 3D viewer", mesh.GetPathName());
            Log(LogKind.Error, $"3D viewer: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    /// <summary>Settings › Preview's texture size and the game's texture platform, for what the 3D viewer loads.</summary>
    private ViewerLoadContext ViewerContext() => new(Settings.PreviewMaxTextureSize, Settings.CurrentVersioning.PlatformValue);

    /// <summary>
    /// Raised on the UI thread with a material for the 3D viewer: the handler swaps it into the section waiting for
    /// one (FModel's material overwrite) or shows it on a cube.
    /// </summary>
    public event Action<ViewerMaterial, ViewerModel>? MaterialReady;

    // FModel previews material instances on the editor's cube
    public async Task PreviewMaterialAsync(UMaterialInterface material)
    {
        SetStatus(StatusKind.Loading, $"Loading {material.Name} in the 3D viewer");
        try
        {
            var context = ViewerContext();
            var (resolved, cube) = await Task.Run(() => (ViewerModel.ResolveMaterial(material, context), ViewerModel.FromMaterial(material, context)));
            MaterialReady?.Invoke(resolved, cube);
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not load {Material} in the 3D viewer", material.GetPathName());
            Log(LogKind.Error, $"3D viewer: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    /// <summary>Raised on the UI thread with a loaded map, ready for the 3D viewer.</summary>
    public event Action<SceneItem[]>? MapReady;

    // Snooper loads a whole level too: every mesh, instanced foliage and landscape, placed as in game
    public async Task PreviewMapAsync(UWorld world)
    {
        SetStatus(StatusKind.Loading, $"Loading {world.Name}");
        var progress = new Progress<string>(message => SetStatus(StatusKind.Loading, message));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var context = ViewerContext();
            var map = await Task.Run(() => MapLoader.Load(world, progress, context: context));
            MapReady?.Invoke(map);
            Log(LogKind.Information, $"Loaded map '{map[0].Name}' in the 3D viewer in {watch.Elapsed.TotalSeconds:0.0}s: {map[0].Details[6..]}" +
                                     (map.Length > 1 ? $" (sky and backdrops hidden: {map[1].Details})" : ""));
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not load map {Map} in the 3D viewer", world.GetPathName());
            Log(LogKind.Error, $"3D viewer: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    /// <summary>Raised on the UI thread with a decoded animation; the handler returns an error message or null.</summary>
    public Func<IViewerMotion, string?>? AnimationReady { get; set; }

    // Snooper plays an animation on the skeletal mesh already open; same here
    public async Task PreviewAnimationAsync(UAnimationAsset asset)
    {
        if (AnimationReady is null) return;
        SetStatus(StatusKind.Loading, $"Decoding {asset.Name}");
        try
        {
            var animation = await Task.Run(() => DecodeMotion(asset));
            if (AnimationReady(animation) is { } error)
            {
                Log(LogKind.Warning, error);
                SetStatus(StatusKind.Completed, "Completed");
                return;
            }
            Log(LogKind.Information, animation is ViewerBlendSpace blend
                ? $"Playing blend space '{blend.Name}' in the 3D viewer ({blend.Samples.Length} samples)"
                : $"Playing '{animation.Name}' in the 3D viewer ({animation.Clips[0].Duration:0.00} s)");
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not play {Animation} in the 3D viewer", asset.GetPathName());
            Log(LogKind.Error, $"3D viewer: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    private IViewerMotion DecodeMotion(UAnimationAsset asset) => asset is UBlendSpaceBase blendSpace
        ? ViewerBlendSpace.FromExport(blendSpace)
        : ViewerAnimation.FromExport(asset, _animations is { } library ? library.SoundByName : null);

    // ---------- The 3D viewer's animation list and notifies ----------

    /// <summary>
    /// The animations made for a skinned model's skeleton (see <see cref="AnimationLibrary"/>), on a worker thread;
    /// <paramref name="partial"/> may get a first answer (from the worker thread) while a pak game's index is built.
    /// </summary>
    public Task<IReadOnlyList<AnimationEntry>> FindAnimationsAsync(ViewerModel model, Action<IReadOnlyList<AnimationEntry>>? partial,
        IProgress<double>? indexProgress, CancellationToken ct)
    {
        if (_animations is not { } library || model.SkeletonPath is null) return Task.FromResult<IReadOnlyList<AnimationEntry>>([]);
        var skeletons = new[] { model.SkeletonPath }.Concat(model.CompatibleSkeletons).ToArray();
        return Task.Run(() => library.Find(skeletons, partial, indexProgress, ct), ct);
    }

    /// <summary>Decodes an animation, montage, composite or blend space for the viewer, with its notifies.</summary>
    public Task<IViewerMotion> LoadMotionAsync(GameFile file)
    {
        var archive = _archive ?? throw new InvalidOperationException("No game is loaded");
        return Task.Run(() =>
        {
            var asset = AssetPreview.LoadExports(archive.Provider, file).OfType<UAnimationAsset>()
                            .FirstOrDefault(a => a is UAnimSequence or UAnimMontage or UAnimComposite or UBlendSpaceBase)
                        ?? throw new InvalidOperationException($"{file.Name} has no animation");
            return DecodeMotion(asset);
        });
    }

    /// <summary>The sound a notify plays (a sound wave, cue, Wwise event…), decoded once and kept; null when it can't be.</summary>
    public Task<PcmAudio?> DecodeNotifySoundAsync(string path) => _notifySounds.GetOrAdd(path, p => Task.Run(() => DecodeNotifySound(p)));

    private PcmAudio? DecodeNotifySound(string path)
    {
        if (_archive is not { } archive || _sounds is not { } extractor || _animations is not { } library) return null;
        try
        {
            if (library.PackageOf(path) is not { } file) return null;
            var exports = AssetPreview.LoadExports(archive.Provider, file);
            List<ExtractedSound> sounds;
            lock (extractor) sounds = extractor.FromExports(file, exports);
            // A sound cue holds no audio: play the first wave it references
            if (sounds.Count == 0)
            {
                foreach (var wave in exports.SelectMany(e => NotifyReader.References(e, c => c.StartsWith("SoundWave", StringComparison.Ordinal))))
                {
                    if (library.PackageOf(wave.GetPathName()) is not { } waveFile) continue;
                    lock (extractor) sounds = extractor.FromExports(waveFile, AssetPreview.LoadExports(archive.Provider, waveFile));
                    if (sounds.Count > 0) break;
                }
            }
            return sounds.FirstOrDefault() is { } sound ? AudioDecoder.Decode(sound.Data, sound.Extension, Settings.CriwareKeyValue) : null;
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not decode the notify sound {Sound}", path);
            return null;
        }
    }

    /// <summary>A notify's particle system (Niagara or Cascade) as the viewer approximates it, read once and kept.</summary>
    public Task<ViewerEffect?> LoadEffectAsync(string path) => _notifyEffects.GetOrAdd(path, p => Task.Run(() => LoadEffect(p)));

    private ViewerEffect? LoadEffect(string path)
    {
        if (_archive is not { } archive || _animations is not { } library) return null;
        try
        {
            if (library.PackageOf(path) is not { } file) return null;
            var objectName = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : file.NameWithoutExtension;
            var exports = AssetPreview.LoadExports(archive.Provider, file);
            var system = exports.FirstOrDefault(e => e.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                         ?? exports.FirstOrDefault(e => e.ExportType is "NiagaraSystem" or "ParticleSystem");
            return system is null ? null : EffectReader.Read(system, new ViewerLoadContext(256, Settings.CurrentVersioning.PlatformValue));
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not read the effect {Effect}", path);
            return null;
        }
    }

    [RelayCommand]
    private Task ExtractSelected() => SelectedPackage is { } item ? ExtractAsync(item.File) : Task.CompletedTask;

    // ---------- Context menu: Export / Copy ----------

    [RelayCommand]
    private Task ExportProperties(IList? items) => ExportAsync(items, "properties", (file, path) =>
    {
        var exports = AssetPreview.LoadExports(_archive!.Provider, file);
        var target = path + ".json";
        File.WriteAllText(target, JsonConvert.SerializeObject(exports, Formatting.Indented));
        return target;
    });

    // FModel's Export › Decompiled Blueprints: the pseudo C++ of Decompile Blueprint, saved as .cpp
    [RelayCommand]
    private Task ExportCode(IList? items) => ExportAsync(PackagesOnly(items), "decompiled blueprint(s)", (file, path) =>
    {
        if (_archive!.DecompileBlueprint(file) is not { } code) return null;
        var target = path + ".cpp";
        File.WriteAllText(target, code);
        return target;
    });

    private static IList? PackagesOnly(IList? items) => items?.OfType<AssetItem>().Where(i => i.IsPackage).ToArray();

    // FModel's Export › Raw Data: the package's files as they are (.uasset, .uexp, .ubulk…), through the Export Session
    [RelayCommand]
    private async Task ExportRawData(IList? items)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null) return;
        if (ExportSession.IsRunning)
        {
            Log(LogKind.Warning, "The Export Session is running: wait for it to finish, or cancel it, before queueing more");
            return;
        }
        var added = ExportSession.EnqueueRaw(files, _archive.Provider);
        await AfterQueueAsync(added, "package");
    }

    // Textures go through the ExportSession too, for FModel's texture format, quality, HDR and mip options
    [RelayCommand]
    private Task ExportTexture(IList? items) => QueueExportsAsync(items, "texture", e => e is UTexture);

    private async Task ExportAsync(IList? items, string what, Func<GameFile, string, string?> export)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null) return;

        // A folder's worth: in parallel, with one summary instead of a line per file
        var quiet = files.Length > QuietThreshold;
        var token = BulkToken;
        SetStatus(StatusKind.Loading, $"Exporting {files.Length} {what}");
        int saved = 0, empty = 0, failed = 0, done = 0;
        if (quiet)
        {
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string? last = null;
            try
            {
                await Task.Run(() => Parallel.ForEach(files,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token }, file =>
                    {
                        try
                        {
                            var basePath = Settings.ExportPath(file.PathWithoutExtension);
                            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                            if (export(file, basePath) is { } target)
                            {
                                Interlocked.Increment(ref saved);
                                last = target;
                            }
                            else Interlocked.Increment(ref empty);
                        }
                        catch (Exception e)
                        {
                            Interlocked.Increment(ref failed);
                            Serilog.Log.Error(e, "Export failed for {Path}", file.Path);
                            if (errors.Count < MaxBulkErrors) errors.Enqueue($"{file.Name}: {e.Message}");
                        }
                        var n = Interlocked.Increment(ref done);
                        if (n % 25 == 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusLabel = $"Exporting {what} ({n:N0}/{files.Length:N0})");
                    }), token);
            }
            catch (OperationCanceledException)
            {
            }
            foreach (var error in errors) Log(LogKind.Error, error);
            if (last is not null) LastExportPath = last;
            LogBulkSummary(saved, what, empty, failed);
            FinishBulkStatus(saved > 0, token);
            return;
        }
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;
            var basePath = Settings.ExportPath(file.PathWithoutExtension);
            try
            {
                var target = await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                    return export(file, basePath);
                });
                if (target is null)
                {
                    Log(LogKind.Warning, $"Nothing to export in '{file.Name}'");
                    continue;
                }
                saved++;
                Log(LogKind.Information, $"Successfully saved '{Path.GetFileName(target)}'");
                LastExportPath = target;
            }
            catch (Exception e)
            {
                Serilog.Log.Error(e, "Export failed for {Path}", file.Path);
                Log(LogKind.Error, $"{file.Name}: {e.Message}");
            }
        }
        FinishBulkStatus(saved > 0, token);
    }

    /// <summary>More files than this (a folder export) are logged as a summary.</summary>
    private const int QuietThreshold = 10;
    private const int MaxBulkErrors = 5;

    private void LogBulkSummary(int saved, string what, int empty, int failed)
    {
        Log(saved > 0 ? LogKind.Information : LogKind.Warning,
            $"Saved {saved:N0} {what} to {Settings.ExportsDir}" + (empty > 0 ? $"; {empty:N0} had nothing to export" : ""));
        if (failed > 0) Log(LogKind.Error, $"{failed:N0} failed" + (failed > MaxBulkErrors ? $" (the first {MaxBulkErrors} are above, the rest in the log file)" : ""));
    }

    private void FinishBulkStatus(bool any, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            Log(LogKind.Warning, "Canceled");
            SetStatus(StatusKind.Failed, "Canceled");
        }
        else SetStatus(any ? StatusKind.Completed : StatusKind.Failed, any ? "Completed" : "Failed");
    }

    public string? LastExportPath { get; private set; }

    // FModel's Export › Audio: every sound of the selected packages and files, as WAV or as is (Settings › Audio)
    [RelayCommand]
    private async Task ExportAudio(IList? items)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null || _sounds is null) return;

        SetStatus(StatusKind.Loading, $"Exporting audio from {files.Length} file(s)");
        int saved = 0, failed = 0, empty = 0, done = 0;
        var quiet = files.Length > QuietThreshold;
        var token = BulkToken;
        var provider = _archive.Provider;
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;
            if (quiet && ++done % 25 == 0) StatusLabel = $"Exporting audio ({done:N0}/{files.Length:N0})";
            try
            {
                var (sounds, warnings) = await Task.Run(() =>
                {
                    lock (_sounds)
                    {
                        var list = file.IsUePackage
                            ? _sounds.FromExports(file, AssetPreview.LoadExports(provider, file))
                            : SoundExtractor.AudioExtensions.Contains(file.Extension) ? _sounds.FromFile(file, out _) : [];
                        return (list, _sounds.Warnings.ToArray());
                    }
                });
                if (!quiet) foreach (var warning in warnings) Log(LogKind.Warning, warning);
                if (sounds.Count == 0)
                {
                    empty++;
                    if (!quiet && warnings.Length == 0) Log(LogKind.Warning, $"No audio in '{file.Name}'");
                    continue;
                }
                foreach (var sound in sounds)
                {
                    try
                    {
                        var path = await Task.Run(() => AudioExport.Save(sound, Settings));
                        saved++;
                        LastExportPath = path;
                        if (!quiet) Log(LogKind.Information, $"Successfully saved '{Path.GetFileName(path)}'");
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Serilog.Log.Warning(e, "Audio export failed for {Sound}", sound.FileName);
                        if (!quiet || failed <= MaxBulkErrors)
                            Log(e is MissingDecoderException ? LogKind.Warning : LogKind.Error, $"{sound.FileName}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                failed++;
                Serilog.Log.Error(e, "Audio export failed for {Path}", file.Path);
                if (!quiet || failed <= MaxBulkErrors) Log(LogKind.Error, $"{file.Name}: {e.Message}");
            }
        }
        if (quiet) LogBulkSummary(saved, "sound(s)", empty, failed);
        if (token.IsCancellationRequested) FinishBulkStatus(saved > 0, token);
        else
            SetStatus(saved > 0 ? StatusKind.Completed : StatusKind.Failed,
                saved > 0 ? $"Completed: {saved} sound(s)" + (failed > 0 ? $", {failed} failed" : "") : "Failed");
    }

    // Models, animations, worlds and textures go to FModel's Export Session (Views › Export Session)
    [RelayCommand]
    private Task ExportModel(IList? items) => QueueExportsAsync(items, "model",
        e => e is UStaticMesh or USkinnedAsset || Settings.SaveSkeletonAsMesh && e is USkeleton);

    [RelayCommand]
    private Task ExportAnimation(IList? items) => QueueExportsAsync(items, "animation", e => e is UAnimationAsset);

    [RelayCommand]
    private Task ExportWorld(IList? items) => QueueExportsAsync(items, "world", e => e is UWorld);

    // ---------- Export Folder ----------

    private CancellationTokenSource? _bulk;
    private CancellationToken BulkToken => _bulk?.Token ?? CancellationToken.None;

    /// <summary>A folder export is running: Esc cancels it (FModel's ThreadWorker).</summary>
    public bool CanCancel => _bulk is not null || ExportSession.IsRunning;

    /// <summary>FModel's Escape: stops the folder export (or the Export Session) in progress.</summary>
    public void CancelOperation()
    {
        _bulk?.Cancel();
        if (ExportSession.IsRunning) ExportSession.Cancel();
    }

    /// <summary>Every file under a folder, its subfolders included.</summary>
    public static IEnumerable<GameFile> FolderFiles(FolderNode folder) => folder.Files.Concat(folder.Children.SelectMany(FolderFiles));

    /// <summary>
    /// FModel's Export Folder: what Export does for a selection, for every package of the folder and its subfolders.
    /// Models, animations, worlds, textures and raw data go through the Export Session like a selection does.
    /// </summary>
    public async Task ExportFolderAsync(FolderNode folder, FolderExport kind)
    {
        if (_archive is null || _bulk is not null) return;
        var items = FolderFiles(folder).Select(f => new AssetItem(f)).ToList();
        if (items.Count == 0)
        {
            Log(LogKind.Warning, $"'{folder.Name}' has no packages");
            return;
        }
        IAsyncRelayCommand<IList?> command = kind switch
        {
            FolderExport.RawData => ExportRawDataCommand,
            FolderExport.Properties => ExportPropertiesCommand,
            FolderExport.Textures => ExportTextureCommand,
            FolderExport.Code => ExportCodeCommand,
            FolderExport.Models => ExportModelCommand,
            FolderExport.Worlds => ExportWorldCommand,
            FolderExport.Animations => ExportAnimationCommand,
            _ => ExportAudioCommand
        };
        Log(LogKind.Information, $"Exporting {kind} from the {items.Count:N0} files of '{folder.FullPath}' (Esc cancels)");
        _bulk = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        try
        {
            await command.ExecuteAsync(items);
        }
        finally
        {
            _bulk.Dispose();
            _bulk = null;
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    /// <summary>FModel's Export Session: the queue, its options and its last results.</summary>
    public ExportSessionViewModel ExportSession { get; } = new();

    /// <summary>Raised when assets are queued without "Export immediately", so the window can be pointed at.</summary>
    public event Action<int>? ExportsQueued;

    private async Task QueueExportsAsync(IList? items, string what, Func<UObject, bool> filter)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null) return;
        if (ExportSession.IsRunning)
        {
            Log(LogKind.Warning, "The Export Session is running: wait for it to finish, or cancel it, before queueing more");
            return;
        }

        var provider = _archive.Provider;
        var token = BulkToken;
        SetStatus(StatusKind.Loading, $"Queueing {what}");
        try
        {
            // Packages are read in parallel; one that can't be read is skipped (FModel's BulkFolder ignores them)
            var found = new System.Collections.Concurrent.ConcurrentBag<(int Index, UObject[] Exports)>();
            var unreadable = new System.Collections.Concurrent.ConcurrentBag<string>();
            var done = 0;
            await Task.Run(() => Parallel.For(0, files.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token }, i =>
                {
                    try
                    {
                        var matching = AssetPreview.LoadExports(provider, files[i]).Where(filter).ToArray();
                        if (matching.Length > 0) found.Add((i, matching));
                    }
                    catch (Exception e)
                    {
                        Serilog.Log.Warning(e, "Could not read {Path}", files[i].Path);
                        unreadable.Add(files[i].Name);
                    }
                    var n = Interlocked.Increment(ref done);
                    if (files.Length > QuietThreshold && n % 50 == 0)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusLabel = $"Reading packages for {what}s ({n:N0}/{files.Length:N0})");
                }), token);
            if (!unreadable.IsEmpty)
                Log(LogKind.Warning, $"Could not read {unreadable.Count:N0} package(s): {string.Join(", ", unreadable.Take(MaxBulkErrors))}" +
                                     (unreadable.Count > MaxBulkErrors ? ", …" : ""));
            var exports = found.OrderBy(f => f.Index).SelectMany(f => f.Exports).ToArray();
            if (exports.Length == 0)
            {
                Log(LogKind.Warning, $"No {what} found in the selected package(s)");
                SetStatus(StatusKind.Failed, "Failed");
                return;
            }
            var added = ExportSession.Enqueue(exports, provider);
            await AfterQueueAsync(added, what);
        }
        catch (OperationCanceledException)
        {
            FinishBulkStatus(false, token);
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Could not queue {What}", what);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
    }

    private async Task AfterQueueAsync(int added, string what)
    {
        if (Settings.ExportImmediately)
        {
            await RunExportSessionAsync();
            return;
        }
        Log(LogKind.Information, $"Queued {added} {what}(s) in the Export Session ({ExportSession.QueuedCount} waiting): " +
                                 "open Views › Export Session to export them");
        SetStatus(StatusKind.Completed, "Completed");
        ExportsQueued?.Invoke(added);
    }

    /// <summary>Runs the Export Session and logs what it wrote, like FModel's LogSummary.</summary>
    public async Task RunExportSessionAsync()
    {
        if (_archive is null || ExportSession.QueuedCount == 0 || ExportSession.IsRunning) return;
        SetStatus(StatusKind.Loading, "Exporting");
        void OnProgress(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ExportSessionViewModel.CompletedCount))
                StatusLabel = $"Exporting ({ExportSession.CompletedCount} done, {ExportSession.Progress:P0})";
        }
        ExportSession.PropertyChanged += OnProgress;
        try
        {
            var summary = await ExportSession.RunAsync(Settings, Settings.ExportsDir);
            if (summary.Files.Count <= 10)
            {
                foreach (var file in summary.Files) Log(LogKind.Information, $"Successfully saved '{Path.GetFileName(file)}'");
            }
            else
            {
                var counts = summary.Files.GroupBy(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                    .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
                Log(LogKind.Information, $"Successfully exported {string.Join(", ", counts)} to {summary.Directory}");
            }
            if (summary.Files.Count > 0) LastExportPath = summary.Files[^1];
            if (summary.Failed > 0)
                Log(LogKind.Error, $"Failed to export {summary.Failed} asset(s): open Views › Export Session for the details");
            if (summary.Canceled) Log(LogKind.Warning, "Export canceled");
            SetStatus(summary.Files.Count > 0 ? StatusKind.Completed : StatusKind.Failed,
                summary.Canceled ? "Canceled" : summary.Files.Count > 0 ? "Completed" : "Failed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Export Session failed");
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
        }
        finally
        {
            ExportSession.PropertyChanged -= OnProgress;
        }
    }

    [RelayCommand]
    private async Task Copy(string? what)
    {
        if (SelectedPackage?.File is { } file) await CopyAsync(file, what);
    }

    public async Task CopyAsync(GameFile file, string? what)
    {
        if (SetClipboardText is null) return;
        var text = what switch
        {
            "PackageName" => file.Name,
            "DirectoryPath" => file.Directory,
            "PathNoExtension" => file.PathWithoutExtension,
            "NameNoExtension" => Path.GetFileNameWithoutExtension(file.Name),
            _ => file.Path
        };
        await SetClipboardText(text);
    }

    // ---------- Images (right panel context menu) ----------

    [RelayCommand]
    private void OpenImage()
    {
        if (SelectedTab?.ImagePng is not { } png) return;
        var path = Path.Combine(Path.GetTempPath(), $"{SelectedTab.ImageName ?? "image"}.png");
        File.WriteAllBytes(path, png);
        Process.Start(new ProcessStartInfo("open", [path]) { UseShellExecute = false });
    }

    // ---------- Image Merger ----------

    /// <summary>The Image Merger's images, kept while the app runs so textures can be added from their tabs.</summary>
    public ObservableCollection<MergeImage> MergerImages { get; } = [];

    /// <summary>Raised when an image was added from a tab, so the window can be shown.</summary>
    public event Action? MergerImageAdded;

    [RelayCommand]
    private void AddImageToMerger()
    {
        if (SelectedTab?.ImagePng is not { } png) return;
        if (MergeImage.FromBytes(SelectedTab.ImageName ?? SelectedTab.Header, png) is not { } image) return;
        MergerImages.Add(image);
        Log(LogKind.Information, $"Added '{image.Name}' to the Image Merger ({MergerImages.Count} image(s))");
        MergerImageAdded?.Invoke();
    }

    // ---------- Menus ----------

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo("open", [url]) { UseShellExecute = false });
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(Settings.OutputDir);
        AppSettings.RevealInFinder(Settings.OutputDir);
    }

    /// <summary>All files of the mounted game for Packages > Search.</summary>
    public IEnumerable<GameFile> AllFiles() =>
        _archive?.Provider.Files.Values.Where(f => !f.IsUePackagePayload).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        ?? [];
}

public sealed record PackageReferences(GameFile Package, GameFile[] Files);
