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
using FModelMac.Rendering;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModelMac.Services;
using FModelMac.Services.Audio;
using Newtonsoft.Json;
using Serilog;

namespace FModelMac.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxTabs = 20;

    public const string DonateLink = "https://fmodel.app/donate";
    public const string ReleasesLink = "https://github.com/4sval/FModel/releases";
    public const string IssueLink = "https://github.com/4sval/FModel/discussions/categories/q-a";
    public const string DiscordLink = "https://fmodel.app/discord";

    private GameArchive? _archive;
    private SoundExtractor? _sounds;

    public AppSettings Settings { get; } = AppSettings.Load();
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Set by the window: copies text to the system clipboard.</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    public GameArchive? Archive => _archive;
    public FolderTreeViewModel Folders { get; } = new();
    public ObservableCollection<ArchiveItem> Archives { get; } = [];
    public ObservableCollection<AssetItem> Packages { get; } = [];
    public ObservableCollection<AssetTab> Tabs { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public IReadOnlyList<string> LoadingModes { get; } = ["Multiple", "All"];

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
    public async Task InitializeGameAsync()
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
            await RefreshAesAsync();
            var usmap = await ResolveMappingsAsync();
            var versioning = Settings.CurrentVersioning;
            var options = new LoadOptions(Settings.ArchiveDir!, game, Settings.AesKey, Settings.DynamicKeys, usmap, versioning);
            var progress = new Progress<string>(label => StatusLabel = label);
            _archive = await Task.Run(() => GameArchive.Load(options, progress));
            _sounds = new SoundExtractor(_archive.Provider, Settings.ArchiveDir!);

            foreach (var archive in _archive.Archives)
                Archives.Add(new ArchiveItem(archive));

            var total = _archive.Archives.Count;
            Log(LogKind.Information, $"{_archive.ProjectName} ({game}) | Mounted: {_archive.MountedCount}/{total} | " +
                                     $"Files: x{_archive.Provider.Files.Count:N0} | {_archive.LoadTime.TotalSeconds:F2}s");
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
        if (remount) await InitializeGameAsync();
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
        References = null;
        Archives.Clear();
        Packages.Clear();
        Folders.Clear();
        OnSelectedFolderChanged();
        _archive?.Dispose();
        _archive = null;
        StatusExtra = "";
    }

    public void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception e) { Serilog.Log.Warning(e, "Could not save settings"); }
    }

    private void UpdateTitle() =>
        Title = _archive is null ? $"FModelMac ({Version})" : $"FModelMac ({Version}) - {_archive.ProjectName}";

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

        IsBusy = true;
        SetStatus(StatusKind.Loading, "Loading packages");
        try
        {
            var archive = _archive;
            var sw = Stopwatch.StartNew();
            var index = await Task.Run(() => archive.BuildIndex(scope));
            Folders.Populate(index);
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

    // ---------- Folders & Packages tabs ----------

    private void OnSelectedFolderChanged()
    {
        PackageSearchText = "";
        RefreshPackages();
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

        var tab = TargetTab(newTab);
        tab.Reset(file);
        var cts = tab.Loading = new CancellationTokenSource();
        var provider = _archive.Provider;
        SetStatus(StatusKind.Loading, $"Extracting {file.Name}");

        try
        {
            var sounds = _sounds;
            var result = await Task.Run(() => AssetPreview.Build(provider, file, cts.Token, sounds), cts.Token);
            var image = result.Image is null ? null : await Task.Run(() => new Bitmap(new MemoryStream(result.Image)), cts.Token);
            if (cts.IsCancellationRequested) return;

            tab.Apply(result, image);
            SetStatus(StatusKind.Completed, "Completed");
            foreach (var warning in result.SoundWarnings ?? []) Log(LogKind.Warning, warning);
            if (result.Sounds is { Count: > 0 } found)
            {
                Log(LogKind.Information, found.Count == 1
                    ? $"Found sound '{found[0].FileName}' ({AssetPreview.FormatSize(found[0].Data.Length)})"
                    : $"Found {found.Count} sounds in '{file.Name}'");
                if (Settings.PlayAudioOnExtract) SoundsReady?.Invoke(found);
            }
            if (Settings.PreviewMeshes && result.Mesh is { } mesh) await PreviewMeshAsync(mesh);
            if (Settings.PreviewMeshes && result.Animation is { } animation) await PreviewAnimationAsync(animation);
            if (Settings.PreviewMeshes && result.World is { } world) await PreviewMapAsync(world);
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

    /// <summary>FModel's Show Metadata: the package summary, names, imports and exports in a new tab.</summary>
    public async Task ShowMetadataAsync(GameFile file)
    {
        if (_archive is not { } archive || !file.IsUePackage) return;
        var tab = TargetTab(newTab: SelectedTab?.Entry is not null);
        tab.Reset(file);
        tab.Header = $"{file.Name} (Metadata)";
        var cts = tab.Loading = new CancellationTokenSource();
        SetStatus(StatusKind.Loading, $"Reading the metadata of {file.Name}");
        try
        {
            var json = await Task.Run(() => archive.PackageMetadata(file), cts.Token);
            if (cts.IsCancellationRequested) return;
            tab.ApplyText(json, "Json");
            SetStatus(StatusKind.Completed, "Completed");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            Serilog.Log.Error(e, "Failed to read the metadata of {Path}", file.Path);
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
            var model = await Task.Run(() => ViewerModel.FromExport(mesh));
            ModelReady?.Invoke(model);
            Log(LogKind.Information, $"Loaded '{model.Name}' in the 3D viewer ({model.VertexCount:N0} vertices, " +
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
            var map = await Task.Run(() => MapLoader.Load(world, progress));
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
            IViewerMotion animation = await Task.Run<IViewerMotion>(() => asset is UBlendSpaceBase blendSpace
                ? ViewerBlendSpace.FromExport(blendSpace)
                : ViewerAnimation.FromExport(asset));
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

    [RelayCommand]
    private Task ExportTexture(IList? items) => ExportAsync(items, "texture", (file, path) =>
    {
        var texture = AssetPreview.LoadExports(_archive!.Provider, file).OfType<UTexture>().FirstOrDefault();
        if (texture is null || AssetPreview.EncodeTexture(texture) is not { } png) return null;
        var target = path + ".png";
        File.WriteAllBytes(target, png);
        return target;
    });

    private async Task ExportAsync(IList? items, string what, Func<GameFile, string, string?> export)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null) return;

        SetStatus(StatusKind.Loading, $"Exporting {files.Length} {what}");
        var saved = 0;
        foreach (var file in files)
        {
            var basePath = Path.Combine(Settings.ExportsDir, file.PathWithoutExtension);
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
        SetStatus(saved > 0 ? StatusKind.Completed : StatusKind.Failed, saved > 0 ? "Completed" : "Failed");
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
        int saved = 0, failed = 0;
        var provider = _archive.Provider;
        foreach (var file in files)
        {
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
                foreach (var warning in warnings) Log(LogKind.Warning, warning);
                if (sounds.Count == 0)
                {
                    if (warnings.Length == 0) Log(LogKind.Warning, $"No audio in '{file.Name}'");
                    continue;
                }
                foreach (var sound in sounds)
                {
                    try
                    {
                        var path = await Task.Run(() => AudioExport.Save(sound, Settings));
                        saved++;
                        LastExportPath = path;
                        Log(LogKind.Information, $"Successfully saved '{Path.GetFileName(path)}'");
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log(e is MissingDecoderException ? LogKind.Warning : LogKind.Error, $"{sound.FileName}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                failed++;
                Serilog.Log.Error(e, "Audio export failed for {Path}", file.Path);
                Log(LogKind.Error, $"{file.Name}: {e.Message}");
            }
        }
        SetStatus(saved > 0 ? StatusKind.Completed : StatusKind.Failed,
            saved > 0 ? $"Completed: {saved} sound(s)" + (failed > 0 ? $", {failed} failed" : "") : "Failed");
    }

    // Models, animations and worlds go through CUE4Parse's ExportSession, like FModel's Export Session
    [RelayCommand]
    private Task ExportModel(IList? items) =>
        ExportWithSessionAsync(items, "model", e => e is UStaticMesh or USkinnedAsset, MeshFormat);

    // CUE4Parse can't write animations to glTF, and worlds only to USD
    [RelayCommand]
    private Task ExportAnimation(IList? items) =>
        ExportWithSessionAsync(items, "animation", e => e is UAnimationAsset, MeshFormat is EMeshFormat.Gltf2 ? EMeshFormat.USD : MeshFormat);

    [RelayCommand]
    private Task ExportWorld(IList? items) => ExportWithSessionAsync(items, "world", e => e is UWorld, EMeshFormat.USD);

    private EMeshFormat MeshFormat => Enum.TryParse<EMeshFormat>(Settings.MeshExportFormat, out var f) ? f : EMeshFormat.Gltf2;

    private async Task ExportWithSessionAsync(IList? items, string what, Func<UObject, bool> filter, EMeshFormat format)
    {
        var files = (items?.OfType<AssetItem>() ?? []).Select(i => i.File).ToArray();
        if (files.Length == 0 && SelectedPackage is { } selected) files = [selected.File];
        if (files.Length == 0 || _archive is null) return;

        var provider = _archive.Provider;
        var options = new ExportOptions(format, ENaniteMeshFormat.NaniteLast);
        var session = new ExportSession();
        SetStatus(StatusKind.Loading, $"Exporting {what}");

        try
        {
            var queued = await Task.Run(() =>
            {
                var count = 0;
                foreach (var file in files)
                {
                    foreach (var export in AssetPreview.LoadExports(provider, file).Where(filter))
                    {
                        session.Add(export);
                        count++;
                    }
                }
                return count;
            });
            if (queued == 0)
            {
                Log(LogKind.Warning, $"No {what} found in the selected package(s)");
                SetStatus(StatusKind.Failed, "Failed");
                return;
            }

            var results = await session.RunAsync(Settings.ExportsDir, options);
            var saved = 0;
            foreach (var result in results)
            {
                if (!result.Success)
                {
                    Log(LogKind.Error, $"{result.ObjectPath}: {result.Error?.Message ?? "export failed"}");
                    continue;
                }
                foreach (var path in result.DiskFilePaths ?? [])
                {
                    saved++;
                    LastExportPath = path;
                    Log(LogKind.Information, $"Successfully saved '{Path.GetFileName(path)}'");
                }
            }
            SetStatus(saved > 0 ? StatusKind.Completed : StatusKind.Failed, saved > 0 ? "Completed" : "Failed");
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "{What} export failed", what);
            Log(LogKind.Error, $"{e.GetType().Name}: {e.Message}");
            SetStatus(StatusKind.Failed, "Failed");
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
