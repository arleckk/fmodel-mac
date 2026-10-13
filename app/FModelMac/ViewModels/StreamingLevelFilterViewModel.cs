using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;

namespace FModelMac.ViewModels;

/// <summary>A node of the streaming level tree: a level to export or not, or a group (actor, component) of them.</summary>
public abstract partial class LevelTreeNode : ObservableObject
{
    public required string Name { get; init; }
    public LevelGroupNode? Parent { get; set; }
    public abstract bool? IsChecked { get; set; }

    protected void NotifyParents()
    {
        for (var parent = Parent; parent is not null; parent = parent.Parent) parent.Refresh();
    }
}

/// <summary>One streaming level: checked means exported with the world (CUE4Parse's StreamingLevel.IsPersistent).</summary>
public sealed class LevelNode(StreamingLevel level) : LevelTreeNode
{
    public StreamingLevel Level { get; } = level;

    public override bool? IsChecked
    {
        get => Level.IsPersistent;
        set
        {
            var v = value ?? false;
            if (Level.IsPersistent == v) return;
            Level.IsPersistent = v;
            OnPropertyChanged();
            NotifyParents();
        }
    }
}

/// <summary>An actor or component holding levels: checked, unchecked or mixed, and checking it checks them all.</summary>
public sealed class LevelGroupNode : LevelTreeNode
{
    private bool _updating;

    public ObservableCollection<LevelTreeNode> Children { get; } = [];
    public bool IsExpanded { get; set; }

    public IEnumerable<LevelNode> Levels => Children.SelectMany(c => c is LevelGroupNode g ? g.Levels : [(LevelNode) c]);

    public override bool? IsChecked
    {
        get
        {
            var levels = Levels.ToList();
            var on = levels.Count(l => l.Level.IsPersistent);
            return on == 0 ? false : on == levels.Count ? true : null;
        }
        set
        {
            _updating = true;
            foreach (var level in Levels) level.IsChecked = value ?? false;
            _updating = false;
            RefreshDown();
            NotifyParents();
        }
    }

    public void Add(LevelTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    internal void Refresh()
    {
        if (!_updating) OnPropertyChanged(nameof(IsChecked));
    }

    private void RefreshDown()
    {
        OnPropertyChanged(nameof(IsChecked));
        foreach (var group in Children.OfType<LevelGroupNode>()) group.RefreshDown();
    }
}

/// <summary>
/// FModel's StreamingLevelFilter: a world loads other levels at runtime (sublevels, level instances); the user picks
/// which ones the Export Session exports. Unchecked ones are still referenced from the world's USD.
/// </summary>
public sealed class StreamingLevelFilterViewModel
{
    public string WorldName { get; }
    public int TotalCount { get; }
    public ObservableCollection<LevelTreeNode> Children { get; } = [];
    public IEnumerable<LevelNode> Levels => Children.SelectMany(c => c is LevelGroupNode g ? g.Levels : [(LevelNode) c]);

    public StreamingLevelFilterViewModel(StreamingLevelFilterArgs args)
    {
        WorldName = args.WorldName;
        foreach (var actor in args.Actors)
            if (FromActor(actor) is { } node) Children.Add(node);

        var world = new LevelGroupNode { Name = "Streaming Levels", IsExpanded = true };
        foreach (var level in args.StreamingLevels) world.Add(new LevelNode(level) { Name = level.World.Name });
        if (world.Children.Count > 0) Children.Add(world);
        TotalCount = Levels.Count();
        if (Children.Count == 1 && Children[0] is LevelGroupNode only) only.IsExpanded = true;
    }

    /// <summary>FModel's Skip: export the world without any of them.</summary>
    public void SkipAll()
    {
        foreach (var level in Levels) level.IsChecked = false;
    }

    public void SelectAll()
    {
        foreach (var level in Levels) level.IsChecked = true;
    }

    // Only the branches that lead to levels (FModel lists every component of those actors)
    private static LevelGroupNode? FromActor(ActorDto actor)
    {
        var node = new LevelGroupNode { Name = actor.Name };
        foreach (var level in actor.StreamingLevels ?? []) node.Add(new LevelNode(level) { Name = level.World.Name });
        AddComponent(node, actor.RootComponent);
        return node.Children.Count > 0 ? node : null;
    }

    private static void AddComponent(LevelGroupNode parent, SceneComponentDto? component)
    {
        if (component is null) return;
        foreach (var attached in component.AttachedActors)
            if (FromActor(attached) is { } node) parent.Add(node);
        foreach (var child in component.Children)
        {
            var node = new LevelGroupNode { Name = child.Name };
            AddComponent(node, child);
            if (node.Children.Count > 0) parent.Add(node);
        }
    }
}
