using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;

namespace FModelMac.Rendering;

public static class ViewerMath
{
    /// <summary>UE transform (left-handed, Z-up) to a viewer-space matrix: the same Y/Z swap as the vertices.</summary>
    public static Matrix4x4 ToViewer(FQuat rotation, FVector translation, FVector scale) =>
        Matrix4x4.CreateScale(scale.X, scale.Z, scale.Y) *
        Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(rotation.X, rotation.Z, rotation.Y, -rotation.W))) *
        Matrix4x4.CreateTranslation(translation.X, translation.Z, translation.Y);

    public static Matrix4x4 ToViewer(FTransform transform) => ToViewer(transform.Rotation, transform.Translation, transform.Scale3D);

    /// <summary>Axis-aligned bounds of a box after a transform.</summary>
    public static (Vector3 Min, Vector3 Max) Transform(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            var p = Vector3.Transform(corner, m);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        return (lo, hi);
    }
}

/// <summary>One mesh drawn at one or more places (a map's instanced foliage is a single object).</summary>
public sealed class SceneObject(ViewerModel model, Matrix4x4[] instances)
{
    public ViewerModel Model { get; } = model;
    public Matrix4x4[] Instances { get; private set; } = instances;
    /// <summary>Bumped whenever <see cref="Instances"/> changes, so the viewport re-uploads them.</summary>
    public int Version { get; private set; }
    public AnimationPose? Pose { get; set; }

    public void Translate(Vector3 delta)
    {
        Instances = Instances.Select(m => m * Matrix4x4.CreateTranslation(delta)).ToArray();
        Version++;
    }
}

/// <summary>An entry of the scene outliner: a mesh added by the user, or a whole map.</summary>
public sealed partial class SceneItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Details { get; init; }
    public required SceneObject[] Objects { get; init; }
    public Vector3 Min { get; private set; }
    public Vector3 Max { get; private set; }

    [ObservableProperty] private bool _isVisible = true;

    /// <summary>The mesh when the item is a single model (not a map), for the materials/morphs panel and animations.</summary>
    public SceneObject? Single => Kind != MapKind && Objects.Length == 1 ? Objects[0] : null;

    public const string MapKind = "Map";

    public static SceneItem ForModel(ViewerModel model) => new SceneItem
    {
        Name = model.Name,
        Kind = model.Kind,
        Details = $"{model.Kind} · {model.VertexCount:N0} vertices",
        Objects = [new SceneObject(model, [Matrix4x4.Identity])]
    }.WithBounds();

    internal SceneItem WithBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var obj in Objects)
        foreach (var instance in obj.Instances)
        {
            var (lo, hi) = ViewerMath.Transform(obj.Model.Min, obj.Model.Max, instance);
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
        }
        (Min, Max) = Objects.Length > 0 ? (min, max) : (Vector3.Zero, Vector3.Zero);
        return this;
    }

    public void Translate(Vector3 delta)
    {
        foreach (var obj in Objects) obj.Translate(delta);
        Min += delta;
        Max += delta;
    }
}

/// <summary>Everything the viewport draws. Changes bump <see cref="Version"/>.</summary>
public sealed class ViewerScene
{
    public ObservableCollection<SceneItem> Items { get; } = [];
    public int Version { get; private set; }

    public ViewerScene()
    {
        Items.CollectionChanged += (_, e) =>
        {
            foreach (SceneItem item in e.NewItems ?? Array.Empty<SceneItem>()) item.PropertyChanged += (_, _) => Version++;
            Version++;
        };
    }

    public IEnumerable<SceneObject> VisibleObjects => Items.Where(i => i.IsVisible).SelectMany(i => i.Objects);

    public (Vector3 Min, Vector3 Max)? Bounds(IEnumerable<SceneItem>? items = null)
    {
        var list = (items ?? Items.Where(i => i.IsVisible)).ToList();
        if (list.Count == 0) return null;
        return (list.Select(i => i.Min).Aggregate(Vector3.Min), list.Select(i => i.Max).Aggregate(Vector3.Max));
    }

    /// <summary>Adds a model beside what's already there (on +X), so several meshes can be compared.</summary>
    public SceneItem AddBeside(SceneItem item)
    {
        if (Bounds() is { } scene)
        {
            var gap = Math.Max((item.Max - item.Min).X, 20) * 0.25f;
            item.Translate(new Vector3(scene.Max.X + gap - item.Min.X, 0, 0));
        }
        Items.Add(item);
        return item;
    }
}

