using System.Collections.Concurrent;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Assets.Exports.WorldPartition.DataLayer;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;

namespace FModelMac.Rendering;

/// <summary>
/// Turns a UWorld into scene items: persistent level actors (through CUE4Parse's <see cref="WorldDto"/>, which
/// resolves component attachment), instanced meshes, landscapes, streaming levels, level instances and World
/// Partition runtime cells. Each distinct mesh (with its material overrides) is converted once and drawn with all
/// its placements as instances.
/// World Partition content is split the way the game streams it: the main partition (spatially and always loaded
/// cells), one item per runtime data layer (visible when the layer starts activated) and one per HLOD layer
/// (hidden: HLODs are the low-detail stand-ins for the same actors).
/// </summary>
public static class MapLoader
{
    /// <summary>One outliner entry being filled: the placements of each mesh.</summary>
    private sealed class Layer(string name, string kind, bool visible, int order)
    {
        public readonly string Name = name;
        public readonly string Kind = kind;
        public readonly bool Visible = visible;
        public readonly int Order = order;
        public readonly Dictionary<string, List<Matrix4x4>> Instances = new(StringComparer.OrdinalIgnoreCase);
        public int Actors, Levels, Cells;
    }

    /// <summary>A World Partition runtime cell to stream in, and the partition (grid) it belongs to.</summary>
    private sealed record CellRef(FPackageIndex Cell, string Partition, bool IsHlod, DataLayerTable DataLayers);

    /// <param name="context">Texture size and platform; maps keep textures small (512) whatever the setting allows.</param>
    public static SceneItem[] Load(UWorld world, IProgress<string>? progress = null, CancellationToken ct = default, ViewerLoadContext? context = null)
    {
        var builders = new ConcurrentDictionary<string, Func<ViewerLoadContext, ViewerModel>>(StringComparer.OrdinalIgnoreCase);
        var visitedWorlds = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var layers = new ConcurrentDictionary<string, Layer>();
        var main = layers[""] = new Layer(world.Name, SceneItem.MapKind, true, 0);
        var pendingCells = new ConcurrentQueue<CellRef>();
        var failedCells = 0;

        void Place(Layer layer, string key, Func<ViewerLoadContext, ViewerModel> build, Matrix4x4 transform)
        {
            builders.TryAdd(key, build);
            lock (layer)
            {
                if (!layer.Instances.TryGetValue(key, out var list)) layer.Instances[key] = list = [];
                list.Add(transform);
            }
        }

        void VisitWorld(UWorld w, Matrix4x4 parent, Layer layer)
        {
            if (!visitedWorlds.TryAdd(w.GetPathName(), 0)) return;
            Interlocked.Increment(ref layer.Levels);
            using (var dto = new WorldDto(w, expandWorldPartition: false, ct))
            {
                foreach (var actor in dto.Actors) VisitActor(actor, parent, layer);
            }

            // Sub-levels, with their level transform (WorldDto drops it). World Partition's own streaming objects are
            // handled below, cell by cell.
            foreach (var ptr in w.StreamingLevels ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (ptr.Load() is not ULevelStreaming streaming || streaming is UWorldPartitionLevelStreamingDynamic) continue;
                if (streaming.WorldAsset?.TryLoad<UWorld>(out var sub) != true) continue;
                var offset = ViewerMath.ToViewer(streaming.GetOrDefault("LevelTransform", FTransform.Identity));
                VisitWorld(sub, offset * parent, layer);
            }

            foreach (var cell in RuntimeCells(w)) pendingCells.Enqueue(cell);
        }

        void VisitActor(ActorDto actor, Matrix4x4 parent, Layer layer)
        {
            ct.ThrowIfCancellationRequested();
            if (!actor.IsVisible) return;
            Interlocked.Increment(ref layer.Actors);
            var root = actor.RootComponent is { } component ? VisitComponent(component, parent, layer) : parent;
            foreach (var level in actor.StreamingLevels ?? []) VisitWorld(level.World, root, layer); // level instances
        }

        Matrix4x4 VisitComponent(SceneComponentDto component, Matrix4x4 parent, Layer layer)
        {
            var world = ViewerMath.ToViewer(component.Transform) * parent;
            var visible = component is not PrimitiveComponentDto { IsVisible: false };
            switch (component)
            {
                case InstancedStaticMeshComponentDto ism when visible:
                    foreach (var t in ism.Transforms) PlaceMesh(ism, ViewerMath.ToViewer(t) * world, layer);
                    break;
                case SplineMeshComponentDto spline when visible && spline.MeshPtr is { IsNull: false }:
                    // Each one is bent along its own spline: one model per component, not per mesh
                    var bent = spline.Component;
                    var overrides = spline.OverrideMaterials;
                    Place(layer, $"spline:{bent.GetPathName()}", ctx => ViewerModel.FromSpline(bent, ctx, overrides, ct), world);
                    break;
                case StaticMeshComponentDto or SkeletalMeshComponentDto when visible:
                    PlaceMesh((MeshComponentDto) component, world, layer);
                    break;
                case LandscapeMeshComponentDto landscape when visible:
                    // Its vertices already include the component's offset in the landscape (as CUE4Parse's exporters assume)
                    var piece = landscape.Component;
                    var local = component.Transform;
                    var placement = ViewerMath.ToViewer(new FTransform(local.Rotation, FVector.ZeroVector, local.Scale3D)) * parent;
                    Place(layer, $"landscape:{piece.GetPathName()}", ctx => ViewerModel.FromLandscape(piece, ctx, ct), placement);
                    break;
            }

            foreach (var child in component.Children) VisitComponent(child, world, layer);
            foreach (var attached in component.AttachedActors) VisitActor(attached, world, layer);
            return world;
        }

        void PlaceMesh(MeshComponentDto component, Matrix4x4 transform, Layer layer)
        {
            var mesh = component.MeshPtr;
            if (mesh is not { IsNull: false }) return;
            var overrides = component.OverrideMaterials;
            var key = mesh.ResolvedObject?.GetPathName() + "|" + string.Join("|", overrides.Select(o => o?.ResolvedObject?.GetPathName()));
            Place(layer, key, ctx => mesh.TryLoad<UObject>(out var export) && ViewerModel.CanView(export)
                ? ViewerModel.FromExport(export, ctx, overrides, ct)
                : throw new InvalidOperationException($"{mesh.Name} isn't a mesh"), transform);
        }

        // The cell's data layers and partition decide which outliner entry it goes to
        Layer LayerFor(CellRef cell, UObject runtimeCell)
        {
            if (cell.IsHlod)
                return layers.GetOrAdd($"hlod:{cell.Partition}", _ => new Layer($"{world.Name} · HLOD {cell.Partition}", "HLOD", false, 2));

            // FDataLayerInstanceNames since UE 5.1, a plain name array before
            var names = ((runtimeCell as UWorldPartitionRuntimeCell)?.DataLayers?.DataLayers
                         ?? runtimeCell.GetOrDefault<FName[]>("DataLayers", [])).Select(n => n.Text).ToArray();
            if (names.Length == 0) return main;
            var resolved = names.Select(cell.DataLayers.Resolve).ToArray();
            return layers.GetOrAdd($"dl:{string.Join("+", names)}", _ => new Layer(
                $"{world.Name} · {string.Join(" + ", resolved.Select(d => d.Label))}", "Data layer",
                resolved.All(d => d.State == EDataLayerRuntimeState.Activated), 1));
        }

        progress?.Report($"Reading {world.Name}");
        VisitWorld(world, Matrix4x4.Identity, main);

        // World Partition: every runtime cell is a generated level, read a few at a time
        var totalCells = 0;
        while (!pendingCells.IsEmpty)
        {
            var batch = new List<CellRef>();
            while (pendingCells.TryDequeue(out var c)) batch.Add(c);
            totalCells += batch.Count;
            var read = 0;
            Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct },
                cell =>
                {
                    try
                    {
                        if (cell.Cell.TryLoad<UObject>(out var runtimeCell) && CellWorld(runtimeCell) is { } cellWorld)
                        {
                            var layer = LayerFor(cell, runtimeCell);
                            Interlocked.Increment(ref layer.Cells);
                            VisitWorld(cellWorld, Matrix4x4.Identity, layer);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref failedCells);
                        Serilog.Log.Warning(e, "Skipping World Partition cell {Cell} of {Map}", cell.Cell.Name, world.Name);
                    }
                    var n = Interlocked.Increment(ref read);
                    if (n % 25 == 0 || n == batch.Count) progress?.Report($"Reading {world.Name}: {n:N0}/{batch.Count:N0} World Partition cells");
                });
        }

        // Convert each distinct mesh once, a few at a time
        context = new ViewerLoadContext(Math.Min(context?.MaxTextureSize ?? 512, 512), context?.Platform ?? ETexturePlatform.DesktopMobile, 256);
        var models = new ConcurrentDictionary<string, ViewerModel>(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var failed = 0;
        Parallel.ForEach(builders, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct },
            pair =>
            {
                try
                {
                    models[pair.Key] = pair.Value(context);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failed);
                    Serilog.Log.Warning(e, "Skipping {Mesh} in map {Map}", pair.Key, world.Name);
                }
                var n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == builders.Count) progress?.Report($"Loading {world.Name}: {n:N0}/{builders.Count:N0} meshes");
            });

        SceneObject[] Objects(Layer layer) => layer.Instances
            .Where(p => models.ContainsKey(p.Key))
            .Select(p => new SceneObject(models[p.Key], p.Value.ToArray()))
            .OrderBy(o => o.Model.Name).ToArray();

        var items = new List<SceneItem>();
        var (sky, rest) = SplitBackdrops(Objects(main));
        var levels = layers.Values.Sum(l => l.Levels);
        var actors = layers.Values.Sum(l => l.Actors);
        var cellText = totalCells > 0 ? $" · {totalCells:N0} WP cells" + (failedCells > 0 ? $" ({failedCells} unreadable)" : "") : "";
        if (rest.Length > 0)
            items.Add(new SceneItem
            {
                Name = world.Name,
                Kind = SceneItem.MapKind,
                Details = $"Map · {levels:N0} level(s){cellText} · {actors:N0} actors · {rest.Length:N0} meshes · " +
                          $"{rest.Sum(o => o.Instances.Length):N0} placements" + (failed > 0 ? $" · {failed} skipped" : ""),
                Objects = rest
            }.WithBounds());

        foreach (var layer in layers.Values.Where(l => l != main).OrderBy(l => l.Order).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var objects = Objects(layer);
            if (objects.Length == 0) continue;
            items.Add(new SceneItem
            {
                Name = layer.Name,
                Kind = SceneItem.MapKind,
                Details = $"{layer.Kind}{(layer.Visible ? "" : " (hidden in game at start)")} · {layer.Cells:N0} cells · " +
                          $"{objects.Length:N0} meshes · {objects.Sum(o => o.Instances.Length):N0} placements",
                Objects = objects,
                IsVisible = layer.Visible
            }.WithBounds());
        }

        if (sky.Length > 0)
            items.Add(new SceneItem
            {
                Name = $"{world.Name} (sky & backdrop)",
                Kind = SceneItem.MapKind,
                Details = $"{sky.Length} oversized mesh(es): {string.Join(", ", sky.Select(o => o.Model.Name).Distinct().Take(3))}",
                Objects = sky,
                IsVisible = false
            }.WithBounds());

        if (items.Count == 0)
            throw new InvalidOperationException($"{world.Name} has no meshes the viewer can show" +
                                                (failedCells > 0 ? $" ({failedCells} World Partition cells couldn't be read)" : ""));
        // Something must be on screen: if only hidden layers have content, show them
        if (!items.Any(i => i.IsVisible))
            foreach (var item in items) item.IsVisible = true;
        return items.ToArray();
    }

    /// <summary>The runtime cells of a World Partition world (empty for a classic level).</summary>
    private static IEnumerable<CellRef> RuntimeCells(UWorld world)
    {
        if (world.PersistentLevel.Load<ULevel>() is not { } level ||
            !level.WorldSettings.TryLoad<AWorldSettings>(out var settings) ||
            settings.WorldPartition?.TryLoad<UWorldPartition>(out var partition) != true ||
            partition.RuntimeHash?.TryLoad<UWorldPartitionRuntimeHash>(out var hash) != true)
            return [];

        var dataLayers = DataLayerTable.Read(level);
        var cells = new List<CellRef>();
        switch (hash)
        {
            // UE 5.3+: named runtime partitions, HLOD layers being partitions of their own
            case UWorldPartitionRuntimeHashSet set:
            {
                var hlod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var setup in set.RuntimePartitions.SelectMany(d => d.HLODSetups))
                {
                    // HLODIndex is INDEX_NONE (so not serialized) for the main partition layer
                    if (setup.PartitionLayer.TryLoad<URuntimePartition>(out var layer) && layer.TryGetValue(out int _, "HLODIndex"))
                    {
                        hlod.Add(layer.Name.Text);
                        if (!setup.Name.IsNone) hlod.Add(setup.Name.Text);
                    }
                }
                foreach (var data in set.RuntimeStreamingData)
                {
                    var name = data.Name.Text;
                    var isHlod = hlod.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
                    foreach (var cell in data.SpatiallyLoadedCells.Concat(data.NonSpatiallyLoadedCells))
                        cells.Add(new CellRef(cell, name, isHlod, dataLayers));
                }
                break;
            }
            // UE 5.0-5.2: square grids (the HLOD ones are generated per HLOD layer)
            case UWorldPartitionRuntimeSpatialHash spatial:
            {
                foreach (var grid in spatial.StreamingGrids)
                {
                    var name = grid.GridName.Text;
                    var isHlod = name.Contains("HLOD", StringComparison.OrdinalIgnoreCase);
                    foreach (var gridLevel in grid.GridLevels)
                    foreach (var layerCell in gridLevel.LayerCells)
                    foreach (var cell in layerCell.GridCells)
                        cells.Add(new CellRef(cell, name, isHlod, dataLayers));
                }
                break;
            }
            default:
                Serilog.Log.Warning("{Map} uses an unknown World Partition runtime hash {Hash}", world.Name, hash.ExportType);
                break;
        }
        return cells;
    }

    /// <summary>The level a runtime cell streams in.</summary>
    private static UWorld? CellWorld(UObject runtimeCell) =>
        runtimeCell.GetOrDefault<FPackageIndex?>("LevelStreaming") is { } ptr &&
        ptr.TryLoad<ULevelStreaming>(out var streaming) &&
        streaming.WorldAsset?.TryLoad<UWorld>(out var w) == true ? w : null;

    /// <summary>The runtime data layers of a World Partition world, by the names cells reference them with.</summary>
    private sealed class DataLayerTable
    {
        public sealed record Entry(string Label, EDataLayerRuntimeState State);

        private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static DataLayerTable Read(ULevel level)
        {
            var table = new DataLayerTable();
            if (!level.WorldDataLayers.TryLoad<UObject>(out var worldDataLayers)) return table;

            var instances = worldDataLayers is AWorldDataLayers typed ? typed.DataLayerInstances : [];
            foreach (var ptr in instances.Concat(worldDataLayers.GetOrDefault<FPackageIndex[]>("DeprecatedDataLayerInstances", [])))
            {
                if (!ptr.TryLoad<UObject>(out var instance)) continue;
                var asset = (instance as UDataLayerInstanceWithAsset)?.DataLayerAsset;
                var label = asset?.ResolvedObject?.Name.Text
                            ?? (instance.TryGetValue(out FName name, "DataLayerLabel", "DataLayerShortName") ? name.Text : null)
                            ?? (instance.TryGetValue(out string text, "DataLayerLabel", "DataLayerShortName") ? text : null)
                            ?? instance.Name;
                var entry = new Entry(label, instance.GetOrDefault("InitialRuntimeState", EDataLayerRuntimeState.Unloaded));
                table._byName[instance.Name] = entry;
                if (asset?.ResolvedObject is { } resolved)
                {
                    table._byName.TryAdd(resolved.Name.Text, entry);
                    table._byName.TryAdd(resolved.GetPathName(), entry);
                }
            }
            return table;
        }

        /// <summary>
        /// Unknown names (external data layers from game features, or layers we couldn't read) count as active:
        /// better to show something the game might hide than to hide what it shows.
        /// </summary>
        public Entry Resolve(string name) => _byName.GetValueOrDefault(name) ?? new Entry(name, EDataLayerRuntimeState.Activated);
    }

    // Sky spheres and distant backdrops are orders of magnitude bigger than everything else
    private static (SceneObject[] Sky, SceneObject[] Level) SplitBackdrops(SceneObject[] objects)
    {
        if (objects.Length == 0) return ([], []);
        float Size(SceneObject o) => o.Instances.Max(m =>
        {
            var (lo, hi) = ViewerMath.Transform(o.Model.Min, o.Model.Max, m);
            return (hi - lo).Length();
        });

        var sizes = objects.Select(Size).ToArray();
        var sorted = sizes.Order().ToArray();
        var p90 = sorted[(int) ((sorted.Length - 1) * 0.9)];
        const float minimum = 500 * 100; // 500 m: smaller things are part of the level
        var isSky = sizes.Select(size => size > minimum && size > p90 * 5).ToArray();
        return (objects.Where((_, i) => isSky[i]).ToArray(), objects.Where((_, i) => !isSky[i]).ToArray());
    }
}
