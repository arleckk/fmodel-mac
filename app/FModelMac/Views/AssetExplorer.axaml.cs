using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FModelMac.Controls;
using FModelMac.ViewModels;

namespace FModelMac.Views;

/// <summary>
/// FModel's new asset explorer (TiledExplorer / ListExplorer). The tiles are laid out in rows of a virtualized list,
/// so only what's on screen exists, and the explorer resolves just those files.
/// </summary>
public partial class AssetExplorer : UserControl
{
    private const double TileWidth = 149; // 141 + 2 × 4 margin

    private MainViewModel? _vm;

    public AssetExplorer()
    {
        InitializeComponent();
        ExplorerBreadcrumb.SegmentClicked += (_, path) => _vm?.NavigateToFolder(path);
        RowsList.ContainerPrepared += (_, e) => ForEachFile(e.Container, file => Explorer?.Realize(file));
        RowsList.ContainerClearing += (_, e) => ForEachFile(e.Container, file => Explorer?.Unrealize(file));
        RowsList.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateColumns();
        };
        AddHandler(KeyDownEvent, OnExplorerKeyDown, RoutingStrategies.Tunnel);
    }

    private ExplorerViewModel? Explorer => _vm?.Explorer;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Explorer.FolderShown -= OnFolderShown;
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.Explorer.FolderShown += OnFolderShown;
            UpdateColumns();
        }
    }

    private void OnFolderShown()
    {
        if (RowsList.ItemCount > 0) RowsList.ScrollIntoView(0);
    }

    private void UpdateColumns()
    {
        if (Explorer is not { } explorer || RowsList.Bounds.Width <= 0) return;
        explorer.Columns = Math.Max(1, (int) ((RowsList.Bounds.Width - 14) / TileWidth)); // room for the scroll bar
    }

    // A row container shows these files; the row is its DataContext
    private static void ForEachFile(Control container, Action<ExplorerFile> action)
    {
        if (container.DataContext is not ExplorerRow row) return;
        foreach (var file in row.Entries.OfType<ExplorerFile>()) action(file);
    }

    /// <summary>Puts the keyboard focus in the explorer's search box (FModel's Ctrl+F with the explorer open).</summary>
    public void FocusSearch()
    {
        if (SearchBox.FindDescendantOfType<TextBox>() is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    // ---------- Mouse ----------

    private void OnEntryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Explorer is not { } explorer || sender is not Control { DataContext: ExplorerEntry entry } control) return;
        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsRightButtonPressed)
        {
            explorer.ContextClick(entry);
            control.ContextMenu = BuildContextMenu(entry);
            control.ContextMenu.Open(control);
        }
        else if (point.Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            explorer.Click(entry, e.KeyModifiers);
        }
        RowsList.Focus();
        e.Handled = true; // the row list keeps no selection of its own
    }

    private async void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ExplorerEntry entry }) await OpenAsync(entry);
    }

    // FModel: a file opens in the current tab (the explorer gives way to the tabs); a folder is entered, skipping
    // folders that only hold one folder
    private async Task OpenAsync(ExplorerEntry entry)
    {
        if (_vm is not { } vm) return;
        switch (entry)
        {
            case ExplorerFile:
                var files = vm.Explorer.SelectedFiles.ToList();
                if (!files.Contains(entry)) files = [(ExplorerFile) entry];
                vm.SelectedLeftTab = 2;
                for (var i = 0; i < files.Count; i++) await vm.ExtractAsync(files[i].File, newTab: i > 0);
                break;
            case ExplorerFolder folder:
                var node = folder.Node;
                while (node is { FolderCount: 1, PackageCount: 0 }) node = node.Children[0];
                vm.NavigateToFolder(node.FullPath);
                vm.SelectedLeftTab = 1;
                break;
        }
    }

    // ---------- Keyboard ----------

    private async void OnExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (Explorer is not { } explorer || e.Source is TextBox) return;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        ExplorerEntry? moved = null;
        switch (e.Key)
        {
            case Key.Left: moved = explorer.Move(-1, 0, extend); break;
            case Key.Right: moved = explorer.Move(1, 0, extend); break;
            case Key.Up: moved = explorer.Move(0, -1, extend); break;
            case Key.Down: moved = explorer.Move(0, 1, extend); break;
            case Key.A when e.KeyModifiers == KeyModifiers.Meta:
                explorer.SelectAll();
                break;
            case Key.Enter when explorer.Primary is { } primary:
                e.Handled = true;
                await OpenAsync(primary);
                return;
            case Key.Back when e.KeyModifiers == KeyModifiers.None && _vm?.SelectedFolder is { } current:
                // Up one folder, like Finder's ⌘↑
                var parent = current.FullPath.Contains('/') ? current.FullPath[..current.FullPath.LastIndexOf('/')] : null;
                if (parent is not null) _vm.NavigateToFolder(parent);
                break;
            default:
                return;
        }
        e.Handled = true;
        if (moved is not null && explorer.RowOf(moved) is var row and >= 0) RowsList.ScrollIntoView(row);
    }

    // ---------- Context menu (the Packages list's, for the selected files) ----------

    private ContextMenu BuildContextMenu(ExplorerEntry entry)
    {
        var vm = _vm!;
        var menu = new ContextMenu();
        if (entry is ExplorerFolder folder)
        {
            menu.Items.Add(Item("Open", "FolderIcon", async () => await OpenAsync(folder)));
            var exportFolder = Item("Export Folder", "ExportIcon", null);
            foreach (var (header, icon, kind) in new[]
                     {
                         ("Raw Data (.uasset)", "AssetIcon", FolderExport.RawData), ("Properties (.json)", "JsonIcon", FolderExport.Properties),
                         ("Textures", "TextureIcon", FolderExport.Textures), ("Decompiled Blueprints", "CodeIcon", FolderExport.Code),
                         ("Models", "ModelIcon", FolderExport.Models), ("Worlds", "WorldIcon", FolderExport.Worlds),
                         ("Animations", "AnimationIcon", FolderExport.Animations), ("Audio", "AudioIcon", FolderExport.Audio)
                     })
                exportFolder.Items.Add(Item(header, icon, async () => await vm.ExportFolderAsync(folder.Node, kind)));
            menu.Items.Add(exportFolder);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy Directory Path", "CopyIcon", async () =>
            {
                if (vm.SetClipboardText is { } copy) await copy(folder.Node.FullPath);
            }));
            return menu;
        }

        var files = vm.Explorer.SelectedFiles.ToList();
        var items = files.Select(f => f.Item).ToList();
        var packages = files.Where(f => f.File.IsUePackage).ToList();
        menu.Items.Add(Item("Extract in New Tab", "ExtractIcon", async () =>
        {
            vm.SelectedLeftTab = 2;
            foreach (var file in files) await vm.ExtractAsync(file.File, newTab: true);
        }));
        menu.Items.Add(Item("Show Metadata", "InfoIcon", async () =>
        {
            foreach (var file in packages) await vm.ShowMetadataAsync(file.File);
        }, packages.Count > 0));
        menu.Items.Add(Item("Find References", "SearchIcon", async () => await vm.FindReferencesAsync(packages[0].File), packages.Count > 0));
        menu.Items.Add(Item("Decompile Blueprint", "BlueprintIcon", async () =>
        {
            foreach (var file in packages) await vm.DecompileAsync(file.File);
        }, packages.Count > 0));
        menu.Items.Add(new Separator());

        var export = Item("Export", "ExportIcon", null);
        void AddExport(string header, string icon, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<System.Collections.IList?> command) =>
            export.Items.Add(Item(header, icon, async () => await command.ExecuteAsync(items)));
        AddExport("Raw Data (.uasset)", "AssetIcon", vm.ExportRawDataCommand);
        AddExport("Properties (.json)", "JsonIcon", vm.ExportPropertiesCommand);
        AddExport("Texture", "TextureIcon", vm.ExportTextureCommand);
        AddExport("Model", "ModelIcon", vm.ExportModelCommand);
        AddExport("World", "WorldIcon", vm.ExportWorldCommand);
        AddExport("Animation", "AnimationIcon", vm.ExportAnimationCommand);
        AddExport("Audio", "AudioIcon", vm.ExportAudioCommand);
        AddExport("Decompiled Blueprints (.cpp)", "CodeIcon", vm.ExportCodeCommand);
        menu.Items.Add(export);

        var copy = Item("Copy", "CopyIcon", null);
        foreach (var (header, what) in new[]
                 {
                     ("Package Path", "PackagePath"), ("Package Name", "PackageName"), ("Directory Path", "DirectoryPath"),
                     ("Package Path w/o Extension", "PathNoExtension"), ("Package Name w/o Extension", "NameNoExtension")
                 })
            copy.Items.Add(new MenuItem { Header = header, Command = vm.CopyCommand, CommandParameter = what });
        menu.Items.Add(copy);
        return menu;
    }

    private static MenuItem Item(string header, string icon, Func<Task>? action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (Application.Current?.TryGetResource(icon, null, out var geometry) == true)
            item.Icon = new Glyph { Data = geometry as Avalonia.Media.Geometry };
        if (action is not null) item.Click += async (_, _) => await action();
        return item;
    }
}
