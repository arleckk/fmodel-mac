using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using FModel.Services;

namespace FModel.ViewModels;

/// <summary>A tile (or row) of the asset explorer: a subfolder or a file of the folder shown.</summary>
public abstract partial class ExplorerEntry : ObservableObject
{
    public abstract string Name { get; }
    [ObservableProperty] private bool _isSelected;

    internal static Geometry? IconGeometry(string key) =>
        Application.Current?.TryGetResource(key, null, out var resource) == true ? resource as Geometry : null;
}

public sealed class ExplorerFolder : ExplorerEntry
{
    public ExplorerFolder(FolderNode node)
    {
        Node = node;
        if (AssetCategories.FolderIconFor(node.Name) is var (icon, color))
        {
            Badge = IconGeometry(icon);
            BadgeBrush = new SolidColorBrush(color);
        }
    }

    public FolderNode Node { get; }
    public override string Name => Node.Name;
    public string SubfoldersText => $"Subfolders: {Node.FolderCount:N0}";
    public string AssetsText => $"Assets: {Node.PackageCount:N0}";
    public Geometry? Badge { get; }
    public IBrush? BadgeBrush { get; }
    public bool HasBadge => Badge is not null;
}

public sealed partial class ExplorerFile : ExplorerEntry
{
    public ExplorerFile(GameFile file, AssetInfo info)
    {
        File = file;
        Item = new AssetItem(file);
        Apply(info);
    }

    public GameFile File { get; }
    /// <summary>The same item the Packages list uses, for its commands (export, copy…).</summary>
    public AssetItem Item { get; }
    public override string Name => File.NameWithoutExtension;
    public string SizeText => AssetPreview.FormatSize(File.Size);

    [ObservableProperty] private AssetCategory _category;
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasThumbnail), nameof(ShowIcon))] private Bitmap? _thumbnail;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLayers))] private int _layers;
    [ObservableProperty] private Geometry? _icon;
    [ObservableProperty] private IBrush _iconBrush = Brushes.White;

    public bool HasThumbnail => Thumbnail is not null;
    public bool ShowIcon => Thumbnail is null;
    public bool HasLayers => Layers > 1;

    // Resolution state, touched on the UI thread only
    internal bool IsResolved;          // category from the package (not just the extension)
    internal bool HasResolvedThumbnail; // thumbnail looked for (found or not)
    internal bool IsQueued;
    internal bool IsRealized;           // shown on screen now

    internal void Apply(AssetInfo info)
    {
        Category = info.Category;
        TypeName = info.TypeName;
        Layers = info.Layers;
        var (icon, color) = AssetCategories.IconFor(info.Category, info.TypeName, info.Family);
        Icon = IconGeometry(icon);
        IconBrush = new SolidColorBrush(color);
    }
}

/// <summary>A line of the virtualized explorer: as many tiles as fit the width (one entry in list view).</summary>
public sealed class ExplorerRow(IReadOnlyList<ExplorerEntry> entries, bool isList)
{
    public IReadOnlyList<ExplorerEntry> Entries { get; } = entries;
    public bool IsList { get; } = isList;
    public bool IsGrid => !IsList;
    public ExplorerEntry First => Entries[0];
}

/// <summary>
/// FModel's new asset explorer: the selected folder's subfolders and files as tiles with thumbnails (or a list), with
/// a search box and a category filter. Packages are opened only for what's on screen (their category, type and
/// texture thumbnail), a few at a time in the background, like FModel's GameFileViewModel.ResolveAsync.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private const int Workers = 4;

    private readonly AppSettings _settings;
    private readonly Func<IFileProvider?> _provider;
    private readonly LinkedList<ExplorerFile> _queue = [];
    private readonly object _queueLock = new();
    private int _runningWorkers;
    private int _generation;

    private List<ExplorerEntry> _all = [];
    private List<ExplorerEntry> _visible = [];
    private ExplorerEntry? _anchor;

    public ExplorerViewModel(AppSettings settings, Func<IFileProvider?> provider)
    {
        _settings = settings;
        _provider = provider;
    }

    public FolderNode? Folder { get; private set; }
    public ObservableCollection<ExplorerRow> Rows { get; private set; } = [];
    public IReadOnlyList<ExplorerEntry> Visible => _visible;
    public AssetCategory[] Categories => AssetCategories.BaseCategories;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private AssetCategory _selectedCategory = AssetCategory.All;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _columns = 1;

    public bool HasFolder => Folder is not null;
    public bool IsEmpty => Folder is not null && _visible.Count == 0;

    public bool IsGrid
    {
        get => _settings.ExplorerViewMode == ExplorerViewMode.Grid;
        set
        {
            if (value == IsGrid) return;
            _settings.ExplorerViewMode = value ? ExplorerViewMode.Grid : ExplorerViewMode.List;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsList));
            BuildRows();
        }
    }

    public bool IsList
    {
        get => !IsGrid;
        set => IsGrid = !value;
    }

    /// <summary>FModel's "Preview Textures" toggle: thumbnails of textures and images.</summary>
    public bool PreviewTextures
    {
        get => _settings.PreviewTexturesAssetExplorer;
        set
        {
            if (value == PreviewTextures) return;
            _settings.PreviewTexturesAssetExplorer = value;
            _settings.Save();
            OnPropertyChanged();
            foreach (var file in _all.OfType<ExplorerFile>())
            {
                if (!value) file.Thumbnail = null;
                file.HasResolvedThumbnail = false;
                if (file.IsRealized) Enqueue(file);
            }
        }
    }

    public IEnumerable<ExplorerEntry> SelectedEntries => _visible.Where(e => e.IsSelected);
    public IEnumerable<ExplorerFile> SelectedFiles => SelectedEntries.OfType<ExplorerFile>();

    /// <summary>Raised when the primary selection changes (the Packages tab's INFORMATION follows it).</summary>
    public event Action<ExplorerEntry?>? SelectionChanged;

    /// <summary>Raised when the rows are rebuilt for another folder, so the view scrolls back to the top.</summary>
    public event Action? FolderShown;

    public void ShowFolder(FolderNode? folder)
    {
        _generation++;
        lock (_queueLock) _queue.Clear();
        Folder = folder;
        _anchor = null;
        SearchText = "";
        SelectedCategory = AssetCategory.All;

        var game = _provider()?.Versions.Game ?? CUE4Parse.UE4.Versions.EGame.GAME_UE5_LATEST;
        _all = folder is null
            ? []
            : folder.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(ExplorerEntry (c) => new ExplorerFolder(c))
                .Concat(folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new ExplorerFile(f, AssetCategories.FromExtension(f, game))))
                .ToList();
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasFolder));
        Refilter();
        FolderShown?.Invoke();
        SelectionChanged?.Invoke(null);
    }

    partial void OnSearchTextChanged(string value) => Refilter();

    partial void OnSelectedCategoryChanged(AssetCategory value)
    {
        if (value == AssetCategory.All) Refilter();
        else _ = ResolveAllThenFilterAsync();
    }

    partial void OnColumnsChanged(int value) => BuildRows();

    /// <summary>FModel's Alt+0…9: picks a category of the filter by its position.</summary>
    public void SelectCategory(int index)
    {
        if (index >= 0 && index < Categories.Length) SelectedCategory = Categories[index];
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedCategory = AssetCategory.All;
    }

    private void Refilter()
    {
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var category = SelectedCategory;
        _visible = _all.Where(e =>
        {
            var name = e is ExplorerFile f ? f.File.Name : e.Name;
            var matches = words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
            return matches && (category == AssetCategory.All || e is ExplorerFile file && file.Category.IsOf(category));
        }).ToList();
        foreach (var hidden in _all.Except(_visible)) hidden.IsSelected = false;
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStatus();
        BuildRows();
    }

    private void BuildRows()
    {
        var size = IsGrid ? Math.Max(1, Columns) : 1;
        var rows = new ObservableCollection<ExplorerRow>();
        for (var i = 0; i < _visible.Count; i += size)
            rows.Add(new ExplorerRow(_visible.GetRange(i, Math.Min(size, _visible.Count - i)), !IsGrid));
        Rows = rows;
        OnPropertyChanged(nameof(Rows));
    }

    private void UpdateStatus()
    {
        if (Folder is null)
        {
            Status = "";
            return;
        }
        var folders = _visible.Count(e => e is ExplorerFolder);
        var files = _visible.Count - folders;
        Status = $"{folders:N0} folder{(folders == 1 ? "" : "s")}, {files:N0} file{(files == 1 ? "" : "s")}" +
                 (_visible.Count < _all.Count ? $" (of {_all.Count:N0})" : "");
    }

    /// <summary>Row index of an entry in the current layout.</summary>
    public int RowOf(ExplorerEntry entry) => _visible.IndexOf(entry) is var i and >= 0 ? i / (IsGrid ? Math.Max(1, Columns) : 1) : -1;

    // ---------- Selection ----------

    /// <summary>A click on a tile: alone, added/removed with ⌘, or a range from the last click with ⇧.</summary>
    public void Click(ExplorerEntry entry, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift) && _anchor is not null && _visible.Contains(_anchor))
        {
            var (a, b) = (_visible.IndexOf(_anchor), _visible.IndexOf(entry));
            var (from, to) = (Math.Min(a, b), Math.Max(a, b));
            if (!modifiers.HasFlag(KeyModifiers.Meta)) foreach (var e in _visible) e.IsSelected = false;
            for (var i = from; i <= to; i++) _visible[i].IsSelected = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            entry.IsSelected = !entry.IsSelected;
            _anchor = entry;
        }
        else
        {
            foreach (var e in _visible) e.IsSelected = e == entry;
            _anchor = entry;
        }
        SelectionChanged?.Invoke(entry.IsSelected ? entry : SelectedEntries.FirstOrDefault());
    }

    /// <summary>Right-click: keeps a multiple selection that includes the entry, otherwise selects just it.</summary>
    public void ContextClick(ExplorerEntry entry)
    {
        if (!entry.IsSelected) Click(entry, KeyModifiers.None);
    }

    public void SelectAll()
    {
        foreach (var e in _visible) e.IsSelected = true;
        SelectionChanged?.Invoke(_anchor ?? _visible.FirstOrDefault());
    }

    /// <summary>Arrow keys: moves the selection by one tile, or one row up/down; ⇧ extends it.</summary>
    public ExplorerEntry? Move(int dx, int dy, bool extend)
    {
        if (_visible.Count == 0) return null;
        var current = _anchor is not null && _visible.Contains(_anchor) ? _visible.IndexOf(_anchor) : -1;
        var step = dx + dy * (IsGrid ? Math.Max(1, Columns) : 1);
        var next = current < 0 ? 0 : Math.Clamp(current + step, 0, _visible.Count - 1);
        var entry = _visible[next];
        if (extend && _anchor is not null)
        {
            entry.IsSelected = true;
            SelectionChanged?.Invoke(entry);
        }
        else Click(entry, KeyModifiers.None);
        _anchor = entry;
        return entry;
    }

    public ExplorerEntry? Primary => _anchor is { IsSelected: true } anchor ? anchor : SelectedEntries.FirstOrDefault();

    // ---------- Resolution (category, type, thumbnail) ----------

    /// <summary>A tile came on screen: resolve it soon (the newest first, like scrolling expects).</summary>
    public void Realize(ExplorerFile file)
    {
        file.IsRealized = true;
        Enqueue(file);
    }

    public void Unrealize(ExplorerFile file) => file.IsRealized = false;

    private bool NeedsWork(ExplorerFile file) => !file.IsResolved || PreviewTextures && !file.HasResolvedThumbnail && CanHaveThumbnail(file);

    // Only textures and images (and Fortnite items) have one; for the rest the category answers
    private static bool CanHaveThumbnail(ExplorerFile file) =>
        !file.IsResolved || file.Category is AssetCategory.Texture or AssetCategory.ItemDefinitionBase;

    private void Enqueue(ExplorerFile file)
    {
        if (file.IsQueued || !NeedsWork(file)) return;
        file.IsQueued = true;
        lock (_queueLock) _queue.AddFirst(file);
        StartWorkers();
    }

    private void StartWorkers()
    {
        while (true)
        {
            var running = _runningWorkers;
            if (running >= Workers) return;
            if (Interlocked.CompareExchange(ref _runningWorkers, running + 1, running) != running) continue;
            Task.Run(WorkAsync);
            return;
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                ExplorerFile? file;
                lock (_queueLock)
                {
                    file = _queue.First?.Value;
                    if (file is not null) _queue.RemoveFirst();
                }
                if (file is null) return;
                if (_queue.Count > 0) StartWorkers();

                var generation = _generation;
                var (realized, thumbnail) = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    file.IsQueued = false;
                    return (file.IsRealized, PreviewTextures && !file.HasResolvedThumbnail && CanHaveThumbnail(file));
                });
                if (!realized || generation != _generation) continue;
                var resolved = ResolveSafely(file.File, thumbnail);
                Bitmap? bitmap = null;
                if (resolved.Thumbnail is { } png)
                {
                    try { bitmap = new Bitmap(new MemoryStream(png)); }
                    catch (Exception e) { Serilog.Log.Debug(e, "Bad thumbnail for {Path}", file.File.Path); }
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _generation) return;
                    file.Apply(resolved);
                    file.IsResolved = true;
                    if (thumbnail)
                    {
                        file.HasResolvedThumbnail = true;
                        if (PreviewTextures) file.Thumbnail = bitmap;
                    }
                });
            }
        }
        finally
        {
            Interlocked.Decrement(ref _runningWorkers);
            lock (_queueLock)
                if (_queue.Count > 0) StartWorkers();
        }
    }

    private AssetInfo ResolveSafely(GameFile file, bool thumbnail)
    {
        var provider = _provider();
        var game = provider?.Versions.Game ?? CUE4Parse.UE4.Versions.EGame.GAME_UE5_LATEST;
        if (provider is null) return AssetCategories.FromExtension(file, game);
        try
        {
            return AssetCategories.Resolve(provider, file, thumbnail, _settings.CurrentVersioning.PlatformValue);
        }
        catch (Exception e)
        {
            // FModel marks it resolved anyway: it keeps what the extension says
            Serilog.Log.Debug(e, "Could not resolve {Path} for the explorer", file.Path);
            return AssetCategories.FromExtension(file, game);
        }
    }

    // The category filter needs every file's category, not just the ones on screen (FModel resolves them all)
    private async Task ResolveAllThenFilterAsync()
    {
        var generation = _generation;
        var pending = _all.OfType<ExplorerFile>().Where(f => !f.IsResolved).ToList();
        if (pending.Count > 0)
        {
            Status = $"Reading {pending.Count:N0} packages…";
            var results = await Task.Run(() =>
            {
                var list = new (ExplorerFile File, AssetInfo Info)[pending.Count];
                Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    i => list[i] = (pending[i], generation == _generation ? ResolveSafely(pending[i].File, false) : AssetCategories.FromExtension(pending[i].File, default)));
                return list;
            });
            if (generation != _generation) return;
            foreach (var (file, info) in results)
            {
                file.Apply(info);
                file.IsResolved = true;
            }
        }
        if (generation == _generation) Refilter();
    }
}
