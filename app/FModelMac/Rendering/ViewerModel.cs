using System.Collections.Concurrent;
using System.Numerics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
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
/// <param name="maxTextureSize">FModel's Preview Max Texture Size.</param>
/// <param name="platform">The game's texture platform (Settings › General), for console cooks.</param>
/// <param name="detailTextureSize">Largest normal map / mask size (maps keep them smaller than the color textures).</param>
public sealed class ViewerLoadContext(int maxTextureSize = 1024, ETexturePlatform platform = ETexturePlatform.DesktopMobile,
    int? detailTextureSize = null)
{
    public int MaxTextureSize { get; } = maxTextureSize;
    public int DetailTextureSize { get; } = detailTextureSize ?? maxTextureSize;
    public ETexturePlatform Platform { get; } = platform;
    internal readonly ConcurrentDictionary<string, Lazy<ViewerMaterial>> Materials = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, Lazy<ViewerTexture?>> Textures = new(StringComparer.OrdinalIgnoreCase);
    // Landscapes: layer colors, the diffuse textures of a landscape material, and textures' average colors
    internal readonly ConcurrentDictionary<string, Lazy<LandscapeShading.ResolvedLayer>> LayerColors = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<LandscapeShading.Candidate>>> LandscapeTextures = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, Lazy<Vector3?>> TextureColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The landscape layer colors resolved so far, by "material|layer" (diagnostics).</summary>
    public IEnumerable<(string Key, Vector3 Color, string? Texture, float Tiling)> LandscapeLayerColors =>
        LayerColors.Select(p => (p.Key, p.Value.Value.Color, p.Value.Value.Texture?.Name, p.Value.Value.Tiling));
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

    // Settable for FModel's material overwrite: the next material extracted replaces this section's
    [ObservableProperty] private string _materialName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Details))] private ViewerTexture? _texture;
    [ObservableProperty] private Vector3 _color = new(0.62f);
    /// <summary>Tangent-space normal map (UE's: X right, Y along V, Z out), if the material has one.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Details))] private ViewerTexture? _normalTexture;
    /// <summary>Packed roughness / metallic / occlusion mask, read through <see cref="MaskLayout"/>.</summary>
    [ObservableProperty] private ViewerTexture? _maskTexture;
    [ObservableProperty] private MaskLayout _maskLayout = MaskLayout.None;
    /// <summary>A landscape's paint layers, textured in the shader (the section's texture is then the rest).</summary>
    public LandscapeLayers? Landscape { get; init; }

    public string Details => Landscape is { } landscape ? $"Landscape layers: {string.Join(", ", landscape.Names)}" :
                             (Texture is { } t ? $"{t.Name} ({t.Width}×{t.Height})" : "No diffuse texture") +
                             (NormalTexture is not null ? " · normal map" : "") + (MaskTexture is not null ? $" · {MaskLayout}" : "");

    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Waiting for the next extracted material to replace this one.</summary>
    [ObservableProperty] private bool _isAwaitingMaterial;

    public void ApplyMaterial(ViewerMaterial material)
    {
        MaterialName = material.Name;
        Texture = material.Texture;
        Color = material.Color;
        NormalTexture = material.Normal;
        MaskTexture = material.Masks;
        MaskLayout = material.Layout;
    }
}

/// <summary>
/// Up to four paint layers of a landscape component: <see cref="Weights"/> holds each layer's weight in R, G, B, A
/// (sampled with the section's UVs), and each layer's diffuse repeats every <see cref="Tiling"/> quads of the
/// landscape, as UE's LandscapeCoords node does.
/// </summary>
public sealed record LandscapeLayers(ViewerTexture Weights, ViewerTexture?[] Textures, Vector4 Tiling, string[] Names);

/// <summary>What the viewer draws of a material: its name, diffuse texture (if any), color, normal map and masks.</summary>
public sealed record ViewerMaterial(string Name, ViewerTexture? Texture, Vector3 Color, ViewerTexture? Normal = null,
    ViewerTexture? Masks = null, MaskLayout Layout = default);

/// <summary>
/// Which channel (0–3 for R, G, B, A; -1 for none) of a packed mask texture holds roughness, metallic and ambient
/// occlusion. Games pack them differently (ORM, MRA, SRXO, Fortnite's SpecularMasks…), so it comes from the
/// texture's or the parameter's name; an unknown packing isn't used at all.
/// </summary>
public readonly record struct MaskLayout(int Roughness, int Metallic, int Occlusion)
{
    public static readonly MaskLayout None = new(-1, -1, -1);
    public bool IsNone => Roughness < 0 && Metallic < 0 && Occlusion < 0;

    private static readonly Regex Token = new(@"(?:^|[_ \-])([A-Z]{2,5})$", RegexOptions.Compiled);
    private static readonly Regex Single = new(@"(?:^|[_ \-])(Roughness|Metallic|Metalness|AO|Occlusion|AmbientOcclusion)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static MaskLayout? FromNames(string? parameter, string texture)
    {
        // A suffix spelling the channels: T_Rock_ORM, T_Sand_ORH, T_Grass_RMA, T_Skin_SRXO, Base_MRAO, MSRAO…
        // or naming a single one (T_Cliff_Roughness, T_Wall_AO), read from red
        foreach (var name in new[] { texture, parameter })
        {
            if (name is not null && Single.Match(name.Trim()) is { Success: true } single)
                return single.Groups[1].Value.ToUpperInvariant() switch
                {
                    "ROUGHNESS" => new MaskLayout(0, -1, -1),
                    "METALLIC" or "METALNESS" => new MaskLayout(-1, 0, -1),
                    _ => new MaskLayout(-1, -1, 0)
                };
            if (name is null || Token.Match(name.Trim()) is not { Success: true } match) continue;
            var letters = match.Groups[1].Value.Replace("AO", "O"); // MRAO: metallic, roughness, occlusion
            if (letters.Any(c => "ORMASHXEBC".IndexOf(c) < 0) || !letters.Any(c => c is 'R' or 'M')) continue;
            int r = -1, m = -1, a = -1;
            for (var i = 0; i < letters.Length; i++)
            {
                switch (letters[i])
                {
                    case 'R': r = i; break;
                    case 'M': m = i; break;
                    case 'O' or 'A': a = i; break;
                }
            }
            return new MaskLayout(r, m, a);
        }
        // Fortnite's SpecularMasks parameter, what FModel's shader reads: specular, metallic, roughness. Not CUE4Parse's
        // PM_SpecularMasks guess nor a bare "_S" texture: other games pack those differently (DSR's are roughness-first)
        if (string.Equals(parameter, "SpecularMasks", StringComparison.OrdinalIgnoreCase))
            return new MaskLayout(2, 1, -1);
        return null;
    }

    public override string ToString() => IsNone ? "no masks"
        : string.Join("", new[] { (Roughness, "R"), (Metallic, "M"), (Occlusion, "O") }.Where(c => c.Item1 >= 0).OrderBy(c => c.Item1)
            .Select(c => c.Item2)) + " masks";
}

/// <summary>
/// CPU side of a mesh for the viewer: LOD 0 converted to a Y-up right-handed space
/// (UE is Z-up left-handed, so Y and Z are swapped), plus one diffuse texture per section.
/// </summary>
public sealed class ViewerModel
{
    public const int FloatsPerVertex = 12; // position, normal, uv, tangent (xyz + binormal sign)
    public const int FloatsPerSkin = 8;   // 4 bone indices, 4 weights

    private static readonly Regex DiffuseName = new(CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NormalName = new(CMaterialParams2.RegexNormals, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LikelyDiffuse = new("base|color|colour|albedo|diff|main", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            USkeleton skeleton => FromSkeleton(skeleton),
            _ => throw new NotSupportedException($"{export.ExportType} can't be shown in the 3D viewer")
        };
    }

    /// <summary>
    /// One landscape (terrain) component, in the landscape's space (the vertices already include the component's
    /// offset), colored by its paint layers (<see cref="LandscapeShading"/>).
    /// </summary>
    public static ViewerModel FromLandscape(ULandscapeComponent component, ViewerLoadContext context, CancellationToken ct = default)
    {
        using var dto = new LandscapeMeshDto(component);
        var model = Build(dto, "Landscape", context, null, ct, new Vector3(0.42f, 0.47f, 0.36f));
        try
        {
            if (LandscapeShading.Bake(component, dto.LODs[0], context) is not var (texture, average, uvs, layers)) return model;
            for (var i = 0; i < uvs.Length; i++)
            {
                model.Vertices[i * FloatsPerVertex + 6] = uvs[i].U;
                model.Vertices[i * FloatsPerVertex + 7] = uvs[i].V;
            }
            var sections = model.Sections.Select(s => new ViewerSection
            {
                FirstIndex = s.FirstIndex, IndexCount = s.IndexCount, MaterialName = s.MaterialName, Texture = texture, Color = average,
                Landscape = layers
            }).ToArray();
            return new ViewerModel
            {
                Name = model.Name, Kind = model.Kind, Vertices = model.Vertices, Indices = model.Indices, Sections = sections,
                LodCount = model.LodCount, Min = model.Min, Max = model.Max
            };
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not color the landscape component {Component}", component.GetPathName());
            return model;
        }
    }

    /// <summary>A spline mesh component: its static mesh bent along the component's spline, in the component's space.</summary>
    public static ViewerModel FromSpline(USplineMeshComponent component, ViewerLoadContext context, FPackageIndex?[]? overrideMaterials,
        CancellationToken ct = default)
    {
        using var dto = new StaticMeshDto(component, EMeshQuality.Highest);
        return Build(dto, "Spline Mesh", context, overrideMaterials, ct);
    }

    /// <summary>
    /// FModel's material preview: the material on a cube (the editor's BasicShapes/Cube: 100 units, one UV square
    /// per face), standing on the ground grid.
    /// </summary>
    public static ViewerModel FromMaterial(UMaterialInterface material, ViewerLoadContext context)
    {
        var resolved = ResolveMaterial(material, context);
        var vertices = new List<float>();
        var indices = new List<uint>();
        // Each face: normal and the two in-plane axes (u to the right, v down as textures are), in viewer space
        (Vector3 N, Vector3 U, Vector3 V)[] faces =
        [
            (Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY), (-Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY),
            (Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ)
        ];
        foreach (var (n, u, v) in faces)
        {
            var first = (uint) (vertices.Count / FloatsPerVertex);
            foreach (var (a, b) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
            {
                var p = (n + (a * 2 - 1) * u + (b * 2 - 1) * v) * 50 + new Vector3(0, 50, 0); // resting on the ground grid
                // UE's binormal is -cross(N, T)·sign in this mirrored space: the sign makes it point along +V
                vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, a, b, u.X, u.Y, u.Z, -Vector3.Dot(Vector3.Cross(n, u), v)]);
            }
            // Counter-clockwise seen from outside
            var ccw = Vector3.Dot(Vector3.Cross(u, v), n) > 0;
            indices.AddRange(ccw ? [first, first + 1, first + 2, first, first + 2, first + 3] : [first, first + 2, first + 1, first, first + 3, first + 2]);
        }

        return new ViewerModel
        {
            Name = material.Name,
            Kind = "Material",
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            Sections =
            [
                new ViewerSection
                {
                    FirstIndex = 0, IndexCount = indices.Count, MaterialName = resolved.Name, Texture = resolved.Texture, Color = resolved.Color,
                    NormalTexture = resolved.Normal, MaskTexture = resolved.Masks, MaskLayout = resolved.Layout
                }
            ],
            LodCount = 1,
            Min = new Vector3(-50, 0, -50),
            Max = new Vector3(50, 100, 50)
        };
    }

    /// <summary>The diffuse texture and color the viewer uses for a material.</summary>
    public static ViewerMaterial ResolveMaterial(UMaterialInterface material, ViewerLoadContext context) =>
        context.Materials.GetOrAdd(material.GetPathName(),
            _ => new Lazy<ViewerMaterial>(() => ResolveMaterial(material, context, new Vector3(0.62f)))).Value;

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

    /// <summary>FModel's "Handle Skeletons as Empty Meshes": just the bones, so animations can play on a skeleton.</summary>
    public static ViewerModel FromSkeleton(USkeleton skeleton)
    {
        using var dto = new SkeletonDto(skeleton);
        if (dto.Bones.Length == 0) throw new InvalidOperationException($"{skeleton.Name} has no bones");
        var (min, max) = (dto.Bounds.Min, dto.Bounds.Max);
        return new ViewerModel
        {
            Name = skeleton.Name, Kind = "Skeleton", Vertices = [], Indices = [], Sections = [], LodCount = 0,
            Min = new Vector3(min.X, min.Z, min.Y), Max = new Vector3(max.X, max.Z, max.Y),
            Bones = dto.Bones.Select(b => new ViewerBone(b.Name, b.ParentIndex, b.Transform.Rotation, b.Transform.Translation, b.Transform.Scale3D)).ToArray(),
            Skin = []
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
            var tangent = new Vector3(v.Tangent.X, v.Tangent.Z, v.Tangent.Y);
            if (tangent.LengthSquared() > 1e-8f) tangent = Vector3.Normalize(tangent);
            // The binormal's sign: UE keeps it in TangentZ.W (Nanite and some others in TangentX.W)
            var sign = Math.Abs(v.Normal.W) > 0.5f ? MathF.Sign(v.Normal.W) : Math.Abs(v.Tangent.W) > 0.5f ? MathF.Sign(v.Tangent.W) : 1f;

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
            vertices[o + 8] = tangent.X;
            vertices[o + 9] = tangent.Y;
            vertices[o + 10] = tangent.Z;
            vertices[o + 11] = sign;
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
            var resolved = ResolveMaterial(material, context);
            sections.Add(new ViewerSection
            {
                FirstIndex = section.FirstIndex,
                IndexCount = count,
                MaterialName = material?.Name ?? slot?.SlotName ?? $"Section {sections.Count}",
                Texture = resolved.Texture,
                NormalTexture = resolved.Normal,
                MaskTexture = resolved.Masks,
                MaskLayout = resolved.Layout,
                Color = resolved.Texture is null && untexturedColor is { } terrain ? terrain : resolved.Color
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

    private static ViewerMaterial ResolveMaterial(FPackageIndex? index, ViewerLoadContext context)
    {
        if (index is null || !index.TryLoad<UMaterialInterface>(out var material)) return new ViewerMaterial("", null, new Vector3(0.62f));
        return ResolveMaterial(material, context);
    }

    private static ViewerMaterial ResolveMaterial(UMaterialInterface material, ViewerLoadContext context, Vector3 fallback)
    {
        try
        {
            var parameters = new CMaterialParams2();
            material.GetParams(parameters, EMaterialDepth.TopLayerOnly);

            var normal = parameters.GetTextures(CMaterialParams2.Normals[0]).FirstOrDefault()
                         ?? Fallback(parameters, CMaterialParams2.FallbackNormals)
                         ?? parameters.GetTexturesByRegex(NormalName).FirstOrDefault();

            // The packed masks, with the parameter's name: it often tells the packing when the texture's doesn't.
            // Known parameter names first, then any parameter or texture named after its channels (MSRAO, T_X_ORM…)
            var all = parameters.Textures.Select(p => (Name: p.Key, Texture: p.Value.TryLoad<UTexture>(out var t) ? t : null))
                .Where(p => p.Texture is not null).Select(p => (p.Name, Texture: p.Texture!)).ToList();
            // A normal map is never the masks, even when a parameter says so (DSR's PM_SpecularMasks = T_Rock_N)
            var masks = all.Where(p => p.Texture != normal && !NormalName.IsMatch(p.Texture.Name)).ToList();
            var (maskName, mask) = CMaterialParams2.SpecularMasks[0].Append(CMaterialParams2.FallbackSpecularMasks)
                .Select(name => masks.FirstOrDefault(p => p.Name == name))
                .FirstOrDefault(p => p.Texture is not null);
            var layout = mask is null ? null : MaskLayout.FromNames(maskName, mask.Name);
            if (layout is null && masks.FirstOrDefault(p => MaskLayout.FromNames(p.Name, p.Texture.Name) is not null) is { Texture: not null } named)
            {
                (maskName, mask) = named;
                layout = MaskLayout.FromNames(maskName, mask.Name);
            }

            // Diffuse: the known names, then any color (sRGB) texture that isn't the normal map or the masks
            // ("BaseTexture", "MainTex"…), preferring names that say so; the legacy parameters last
            var texture = parameters.GetTextures(CMaterialParams2.Diffuse[0]).FirstOrDefault()
                          ?? Fallback(parameters, CMaterialParams2.FallbackDiffuse)
                          ?? parameters.GetTexturesByRegex(DiffuseName).FirstOrDefault()
                          ?? all.Where(p => p.Texture.SRGB && p.Texture != normal && p.Texture != mask)
                              .OrderByDescending(p => LikelyDiffuse.IsMatch(p.Name))
                              .Select(p => p.Texture).FirstOrDefault()
                          ?? LegacyDiffuse(material);

            var color = parameters.Colors.TryGetValue(CMaterialParams2.DiffuseColors[0].FirstOrDefault(parameters.Colors.ContainsKey) ?? "", out var c)
                ? new Vector3(c.R, c.G, c.B)
                : fallback;

            return new ViewerMaterial(material.Name, Decoded(texture, context, context.MaxTextureSize), color,
                Decoded(normal, context, context.DetailTextureSize),
                layout is null ? null : Decoded(mask, context, context.DetailTextureSize), layout ?? MaskLayout.None);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not resolve material {Material}", material.GetPathName());
            return new ViewerMaterial(material.Name, null, fallback);
        }
    }

    private static UTexture? Fallback(CMaterialParams2 parameters, string name) =>
        parameters.Textures.TryGetValue(name, out var value) && value.TryLoad<UTexture>(out var texture) ? texture : null;

    internal static ViewerTexture? Decoded(UTexture? texture, ViewerLoadContext context, int maxSize) => texture is null
        ? null
        : context.Textures.GetOrAdd($"{texture.GetPathName()}@{maxSize}",
            _ => new Lazy<ViewerTexture?>(() => Decode(texture, maxSize, context.Platform))).Value;

    // Older/mobile materials sometimes only expose their textures through the first-generation parameters
    private static UTexture? LegacyDiffuse(UMaterialInterface material)
    {
        var parameters = new CMaterialParams();
        material.GetParams(parameters);
        return parameters.Diffuse is { } diffuse && diffuse.TryLoad<UTexture>(out var texture) ? texture : null;
    }

    private static ViewerTexture? Decode(UTexture texture, int maxSize, ETexturePlatform platform)
    {
        if (texture.Decode(maxSize, platform) is not { } decoded) return null;
        using var bitmap = decoded.ToSkBitmap();
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        return rgba is null ? null : new ViewerTexture(texture.Name, rgba.Width, rgba.Height, rgba.Bytes);
    }
}
