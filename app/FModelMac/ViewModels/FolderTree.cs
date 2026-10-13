using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModelMac.Services;

namespace FModelMac.ViewModels;

// One row of FModel's FolderTree: a flattened, virtualized list where depth is shown as indentation
public sealed partial class FolderNode : ObservableObject
{
    private const double IndentWidth = 12;

    private readonly FolderInfo _info;
    private List<FolderNode>? _children;

    public string Name { get; }
    public string FullPath { get; }
    public int Depth { get; }
    public Thickness Indent => new(Depth * IndentWidth, 0, 0, 0);
    public int PackageCount => _info.Files.Count;
    public int FolderCount => _info.SubFolders.Count;
    public bool HasSubFolders => _info.SubFolders.Count > 0;
    public bool HasPackages => _info.Files.Count > 0;
    public IReadOnlyList<GameFile> Files => _info.Files;
    public Bitmap? CustomIcon { get; }
    public bool HasCustomIcon => CustomIcon is not null;
    public string Tooltip => $"{Name} has {FolderCount} folders and {PackageCount} packages";

    // Archive details shown under INFORMATION, taken from the folder's first package like FModel does
    public string ArchiveName => FirstEntry?.Vfs.Name ?? "None";
    public string ArchiveMountPoint => FirstEntry?.Vfs.MountPoint ?? "/";
    public string ArchiveVersion => FirstEntry?.Vfs.Ver.ToString() ?? "VER_UE4_LATEST";
    private VfsEntry? FirstEntry => _info.Files.OfType<VfsEntry>().FirstOrDefault();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowClosedFolder), nameof(ShowOpenFolder))]
    private bool _isExpanded;

    // FModel's default folder drawing (open when expanded), used when the name has no custom icon
    public bool ShowClosedFolder => !HasCustomIcon && !IsExpanded;
    public bool ShowOpenFolder => !HasCustomIcon && IsExpanded;

    public FolderNode(FolderIndex index, FolderInfo info, string fullPath, string name, int depth)
    {
        Index = index;
        _info = info;
        FullPath = fullPath;
        Name = name;
        Depth = depth;
        CustomIcon = AppResources.FolderIcon(name);
    }

    private FolderIndex Index { get; }

    public IReadOnlyList<FolderNode> Children => _children ??= _info.SubFolders
        .Select(sub =>
        {
            var path = FullPath.Length == 0 ? sub : $"{FullPath}/{sub}";
            return new FolderNode(Index, Index.Folders[path], path, sub, Depth + 1);
        })
        .ToList();
}

public sealed partial class FolderTreeViewModel : ObservableObject
{
    private FolderIndex? _index;

    public ObservableCollection<FolderNode> VisibleFolders { get; } = [];

    [ObservableProperty] private FolderNode? _selectedFolder;

    public void Populate(FolderIndex index)
    {
        _index = index;
        SelectedFolder = null;
        VisibleFolders.Clear();
        if (!index.Folders.TryGetValue("", out var root)) return;

        foreach (var node in new FolderNode(index, root, "", "", -1).Children)
            VisibleFolders.Add(node);
    }

    public void Clear()
    {
        _index = null;
        SelectedFolder = null;
        VisibleFolders.Clear();
    }

    public void Toggle(FolderNode node)
    {
        if (node.IsExpanded) Collapse(node);
        else Expand(node);
    }

    public void Expand(FolderNode node)
    {
        if (node.IsExpanded || !node.HasSubFolders) return;
        var at = VisibleFolders.IndexOf(node);
        if (at < 0) return;

        node.IsExpanded = true;
        var insert = at + 1;
        foreach (var child in node.Children)
        {
            VisibleFolders.Insert(insert++, child);
            // Re-show grandchildren of children that were left expanded
            if (child.IsExpanded) insert = InsertExpandedDescendants(child, insert);
        }
    }

    public void Collapse(FolderNode node)
    {
        if (!node.IsExpanded) return;
        node.IsExpanded = false;
        var at = VisibleFolders.IndexOf(node);
        while (at + 1 < VisibleFolders.Count && VisibleFolders[at + 1].Depth > node.Depth)
            VisibleFolders.RemoveAt(at + 1);
    }

    public void CollapseAll()
    {
        if (_index is null) return;
        var selected = SelectedFolder?.FullPath;
        foreach (var node in VisibleFolders) node.IsExpanded = false;
        Populate(_index);
        if (selected is not null) SelectedFolder = VisibleFolders.FirstOrDefault(n => selected.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    public FolderNode? FirstChild(FolderNode node)
    {
        var at = VisibleFolders.IndexOf(node);
        return at >= 0 && at + 1 < VisibleFolders.Count && VisibleFolders[at + 1].Depth > node.Depth
            ? VisibleFolders[at + 1]
            : null;
    }

    public FolderNode? Parent(FolderNode node)
    {
        for (var i = VisibleFolders.IndexOf(node) - 1; i >= 0; i--)
        {
            if (VisibleFolders[i].Depth < node.Depth) return VisibleFolders[i];
        }
        return null;
    }

    /// <summary>Expands every ancestor of <paramref name="path"/> and returns its node.</summary>
    public FolderNode? Reveal(string path)
    {
        FolderNode? node = null;
        var level = (IEnumerable<FolderNode>) VisibleFolders.Where(n => n.Depth == 0).ToList();
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            node = level.FirstOrDefault(n => n.FullPath.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
            if (!current.Equals(path, StringComparison.OrdinalIgnoreCase)) Expand(node);
            level = node.Children;
        }
        return node;
    }

    private int InsertExpandedDescendants(FolderNode node, int insert)
    {
        foreach (var child in node.Children)
        {
            VisibleFolders.Insert(insert++, child);
            if (child.IsExpanded) insert = InsertExpandedDescendants(child, insert);
        }
        return insert;
    }
}
