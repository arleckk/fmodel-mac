using System.Collections.Concurrent;
using System.Numerics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace FModelMac.Rendering;

/// <summary>
/// Shared state while building models: decoded textures and resolved materials are reused across meshes
/// (maps reference the same few hundred textures from thousands of components), and maps use a smaller
/// texture size to keep memory in check. Safe to use from several threads.
/// </summary>
public sealed class ViewerLoadContext(int maxTextureSize = 2048)
{
    public int MaxTextureSize { get; } = maxTextureSize;
    internal readonly ConcurrentDictionary<string, Lazy<(ViewerTexture? Texture, Vector3 Color)>> Materials = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, Lazy<ViewerTexture?>> Textures = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Decoded RGBA8 pixels, top row first, ready for glTexImage2D.</summary>
public sealed record ViewerTexture(string Name, int Width, int Height, byte[] Rgba);

/// <summary>Reference-pose bone in UE space (left-handed, Z-up); <see cref="ViewerAnimation"/> converts it.</summary>
public sealed record ViewerBone(string Name, int ParentIndex, FQuat Rotation, FVector Position, FVector Scale);

/// <summary>
/// A morph target (blend shape) of LOD 0: per-vertex position and normal deltas in viewer space.
/// <see cref="Weight"/> is the manual slider; animation curves with the same name override it.
/// </summary>
public sealed partial class ViewerMorph : ObservableObject
{
    public required string Name { get; init; }
    public required int[] VertexIndices { get; init; }
    public required float[] Deltas { get; init; } // 6 floats per entry: position xyz, normal xyz

    public string Details => VertexIndices.Length > 0 ? $"{VertexIndices.Length:N0} vertices" : "No vertex data in this cook";

    [ObservableProperty] private float _weight;
}

public sealed partial class ViewerSection : ObservableObject
{
    public required int FirstIndex { get; init; }
    public required int IndexCount { get; init; }
    public required string MaterialName { get; init; }
    public ViewerTexture? Texture { get; init; }
    public Vector3 Color { get; init; } = new(0.62f);

    public string Details => Texture is { } t ? $"{t.Name} ({t.Width}×{t.Height})" : "No diffuse texture";

    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>
/// CPU side of a mesh for the viewer: LOD 0 converted to a Y-up right-handed space
/// (UE is Z-up left-handed, so Y and Z are swapped), plus one diffuse texture per section.
/// </summary>
public sealed class ViewerModel
{
    public const int FloatsPerVertex = 8; // position, normal, uv
    public const int FloatsPerSkin = 8;   // 4 bone indices, 4 weights

    private static readonly Regex DiffuseName = new(CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required ViewerSection[] Sections { get; init; }
    public required int LodCount { get; init; }

    /// <summary>Skeletal meshes only: the mesh's reference skeleton and per-vertex skinning data.</summary>
    public ViewerBone[]? Bones { get; init; }
    public float[]? Skin { get; init; }
    public bool IsSkinned => Bones is { Length: > 0 } && Skin is not null;
    public ViewerMorph[] Morphs { get; init; } = [];
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    public int VertexCount => Vertices.Length / FloatsPerVertex;
    public int TriangleCount => Indices.Length / 3;
    public Vector3 Center => (Min + Max) / 2;
    public float Radius => Math.Max((Max - Min).Length() / 2, 1e-3f);

    public static bool CanView(UObject export) => export is UStaticMesh or USkinnedAsset;

    /// <param name="overrideMaterials">Per-slot replacements, as a mesh component in a map sets them.</param>
    public static ViewerModel FromExport(UObject export, ViewerLoadContext? context = null, FPackageIndex?[]? overrideMaterials = null,
        CancellationToken ct = default)
    {
        context ??= new ViewerLoadContext();
        return export switch
        {
            UStaticMesh mesh => Build(new StaticMeshDto(mesh, EMeshQuality.Highest, ENaniteMeshFormat.NoNanite), "Static Mesh", context, overrideMaterials, ct),
            USkinnedAsset mesh => BuildSkinned(WithMorphData(mesh), context, overrideMaterials, ct),
            _ => throw new NotSupportedException($"{export.ExportType} can't be shown in the 3D viewer")
        };
    }

    /// <summary>One landscape (terrain) component, in the component's local space.</summary>
    public static ViewerModel FromLandscape(ULandscapeComponent component, ViewerLoadContext context, CancellationToken ct = default) =>
        Build(new LandscapeMeshDto(component), "Landscape", context, null, ct, new Vector3(0.42f, 0.47f, 0.36f));

    // UE5 desktop cooks strip the morph deltas from UMorphTarget and keep them only in the mesh's GPU
    // buffers; CUE4Parse decodes them back on demand, as its exporter does
    private static SkeletalMeshDto WithMorphData(USkinnedAsset mesh)
    {
        try
        {
            mesh.PopulateMorphTargetVerticesData();
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not decode the morph targets of {Mesh}", mesh.GetPathName());
        }
        return new SkeletalMeshDto(mesh, EMeshQuality.Highest);
    }

    private static ViewerModel BuildSkinned(SkeletalMeshDto mesh, ViewerLoadContext context, FPackageIndex?[]? overrides, CancellationToken ct)
    {
        var model = Build(mesh, "Skeletal Mesh", context, overrides, ct);
        var lod = mesh.LODs.First(l => l.Vertices.Length > 0 && l.Indices.Length > 0);

        // Shaders take 4 influences per vertex: keep the heaviest ones and renormalize
        var skin = new float[lod.Vertices.Length * FloatsPerSkin];
        Span<(int Bone, float Weight)> top = stackalloc (int, float)[4];
        for (var i = 0; i < lod.Vertices.Length; i++)
        {
            top.Clear();
            foreach (var influence in lod.Vertices[i].Influences)
            {
                var slot = 0;
                for (var k = 1; k < 4; k++) if (top[k].Weight < top[slot].Weight) slot = k;
                if (influence.Weight > top[slot].Weight) top[slot] = (influence.Bone, influence.Weight);
            }

            var total = top[0].Weight + top[1].Weight + top[2].Weight + top[3].Weight;
            var o = i * FloatsPerSkin;
            for (var k = 0; k < 4; k++)
            {
                skin[o + k] = top[k].Bone;
                skin[o + 4 + k] = total > 0 ? top[k].Weight / total : 0;
            }
        }

        return new ViewerModel
        {
            Name = model.Name, Kind = model.Kind, Vertices = model.Vertices, Indices = model.Indices, Sections = model.Sections,
            LodCount = model.LodCount, Min = model.Min, Max = model.Max,
            Bones = mesh.Bones.Select(b => new ViewerBone(b.Name, b.ParentIndex, b.Transform.Rotation, b.Transform.Translation, b.Transform.Scale3D)).ToArray(),
            Skin = skin,
            Morphs = LoadMorphs(mesh.MorphTargets, (int) lod.SourceLodIndex, lod.Vertices.Length)
        };
    }

    private static ViewerMorph[] LoadMorphs(FPackageIndex[]? targets, int lodIndex, int vertexCount)
    {
        var morphs = new List<ViewerMorph>();
        foreach (var target in targets ?? [])
        {
            if (!target.TryLoad<UMorphTarget>(out var morph)) continue;
            try
            {
                var deltas = (lodIndex < morph.MorphLODModels.Length ? morph.MorphLODModels[lodIndex].Vertices : [])
                    .Where(d => d.SourceIdx < vertexCount)
                    .ToArray();
                var data = new float[deltas.Length * 6];
                for (var i = 0; i < deltas.Length; i++)
                {
                    var d = deltas[i];
                    data[i * 6] = d.PositionDelta.X;
                    data[i * 6 + 1] = d.PositionDelta.Z;
                    data[i * 6 + 2] = d.PositionDelta.Y;
                    data[i * 6 + 3] = d.TangentZDelta.X;
                    data[i * 6 + 4] = d.TangentZDelta.Z;
                    data[i * 6 + 5] = d.TangentZDelta.Y;
                }
                morphs.Add(new ViewerMorph { Name = morph.Name, VertexIndices = deltas.Select(d => (int) d.SourceIdx).ToArray(), Deltas = data });
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not read morph target {Morph}", morph.GetPathName());
            }
        }
        return morphs.ToArray();
    }

    private static ViewerModel Build<TVertex>(MeshDto<TVertex> mesh, string kind, ViewerLoadContext context, FPackageIndex?[]? overrides,
        CancellationToken ct, Vector3? untexturedColor = null) where TVertex : struct, IMeshVertex
    {
        var lod = mesh.LODs.FirstOrDefault(l => l.Vertices.Length > 0 && l.Indices.Length > 0)
                  ?? throw new InvalidOperationException($"{mesh.Name} has no geometry");

        var vertices = new float[lod.Vertices.Length * FloatsPerVertex];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < lod.Vertices.Length; i++)
        {
            var v = lod.Vertices[i];
            var position = new Vector3(v.Position.X, v.Position.Z, v.Position.Y);
            var normal = new Vector3(v.Normal.X, v.Normal.Z, v.Normal.Y);
            if (normal.LengthSquared() > 1e-8f) normal = Vector3.Normalize(normal);

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);

            var o = i * FloatsPerVertex;
            vertices[o] = position.X;
            vertices[o + 1] = position.Y;
            vertices[o + 2] = position.Z;
            vertices[o + 3] = normal.X;
            vertices[o + 4] = normal.Y;
            vertices[o + 5] = normal.Z;
            vertices[o + 6] = v.Uv.U;
            vertices[o + 7] = v.Uv.V;
        }

        var sections = new List<ViewerSection>();
        foreach (var section in lod.Sections)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(section.NumFaces * 3, lod.Indices.Length - section.FirstIndex);
            if (!section.IsValid || count <= 0) continue;

            var slot = mesh.GetMaterial(section);
            var material = overrides is not null && section.MaterialIndex < overrides.Length && overrides[section.MaterialIndex] is { IsNull: false } o
                ? o
                : slot?.Material;
            var (texture, color) = ResolveMaterial(material, context);
            sections.Add(new ViewerSection
            {
                FirstIndex = section.FirstIndex,
                IndexCount = count,
                MaterialName = material?.Name ?? slot?.SlotName ?? $"Section {sections.Count}",
                Texture = texture,
                Color = texture is null && untexturedColor is { } terrain ? terrain : color
            });
        }

        return new ViewerModel
        {
            Name = mesh.Name,
            Kind = kind,
            Vertices = vertices,
            Indices = lod.Indices,
            Sections = sections.ToArray(),
            LodCount = mesh.LODs.Count,
            Min = min,
            Max = max
        };
    }

    private static (ViewerTexture? Texture, Vector3 Color) ResolveMaterial(FPackageIndex? index, ViewerLoadContext context)
    {
        var fallback = new Vector3(0.62f);
        if (index is null || !index.TryLoad<UMaterialInterface>(out var material)) return (null, fallback);
        return context.Materials.GetOrAdd(material.GetPathName(),
            _ => new Lazy<(ViewerTexture?, Vector3)>(() => ResolveMaterial(material, context, fallback))).Value;
    }

    private static (ViewerTexture? Texture, Vector3 Color) ResolveMaterial(UMaterialInterface material, ViewerLoadContext context, Vector3 fallback)
    {
        try
        {
            var parameters = new CMaterialParams2();
            material.GetParams(parameters, EMaterialDepth.TopLayerOnly);

            var texture = parameters.GetTextures(CMaterialParams2.Diffuse[0]).FirstOrDefault()
                          ?? (parameters.Textures.TryGetValue(CMaterialParams2.FallbackDiffuse, out var fb) && fb.TryLoad<UTexture>(out var t) ? t : null)
                          ?? parameters.GetTexturesByRegex(DiffuseName).FirstOrDefault()
                          ?? LegacyDiffuse(material);

            var color = parameters.Colors.TryGetValue(CMaterialParams2.DiffuseColors[0].FirstOrDefault(parameters.Colors.ContainsKey) ?? "", out var c)
                ? new Vector3(c.R, c.G, c.B)
                : fallback;

            if (texture is null) return (null, color);
            var decoded = context.Textures.GetOrAdd(texture.GetPathName(),
                _ => new Lazy<ViewerTexture?>(() => Decode(texture, context.MaxTextureSize))).Value;
            return (decoded, color);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not resolve material {Material}", material.GetPathName());
            return (null, fallback);
        }
    }

    // Older/mobile materials sometimes only expose their textures through the first-generation parameters
    private static UTexture? LegacyDiffuse(UMaterialInterface material)
    {
        var parameters = new CMaterialParams();
        material.GetParams(parameters);
        return parameters.Diffuse is { } diffuse && diffuse.TryLoad<UTexture>(out var texture) ? texture : null;
    }

    private static ViewerTexture? Decode(UTexture texture, int maxSize)
    {
        if (texture.Decode(maxSize) is not { } decoded) return null;
        using var bitmap = decoded.ToSkBitmap();
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        return rgba is null ? null : new ViewerTexture(texture.Name, rgba.Width, rgba.Height, rgba.Bytes);
    }
}
