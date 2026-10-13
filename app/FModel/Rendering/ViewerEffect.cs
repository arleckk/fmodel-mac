using System.Numerics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Rendering;

/// <summary>
/// One emitter of a particle effect as the viewer draws it: a camera-facing sprite with the emitter material's main
/// texture (a flipbook of <see cref="Columns"/> × <see cref="Rows"/> frames), tinted, added over the scene.
/// </summary>
/// <param name="Texture">Null for materials that draw their shape procedurally (a soft round glow is used).</param>
/// <param name="Size">Half-width in UE units.</param>
/// <param name="Round">The picture has no transparent background (no alpha, no black): faded out round instead.</param>
public sealed record EffectLayer(string Name, ViewerTexture? Texture, Vector3 Tint, int Columns, int Rows, float Size, bool Round = false);

/// <summary>
/// An approximation of a Niagara or Cascade particle system: its emitters' renderers and materials, without the
/// simulation (Niagara's lives in compiled VM scripts), so the viewer can show what an effect looks like and where.
/// </summary>
public sealed record ViewerEffect(string Name, EffectLayer[] Layers);

public static class EffectReader
{
    private const int MaxLayers = 8;
    private const float DefaultSize = 40;

    // Texture parameter names that mean "the picture" or not (noise, distortion, masks…)
    private static readonly Regex MainTexture = new("tex|main|base|diffuse|albedo|colou?r|subuv|flipbook|sprite|particle|fire|flame|smoke|spark|glow|flare|beam|slash|ring|light",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecondaryTexture = new("noise|distort|flow|mask|normal|erode|dissolve|blend|offset|refract|cloud|_n$|placeholder|weightmap",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Materials that bend or darken what's behind them (heat haze, refraction): nothing to add over the scene
    private static readonly Regex Invisible = new("distort|refract|heat|_black|depthonly",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Engine placeholders a particle's own parameters replace at run time: drawn as a soft glow instead of a square
    private static readonly Regex Placeholder = new("^(default|t_white|white|whitesquare|black)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Niagara particle colors come from its compiled scripts: grayscale textures get a color from the effect's name
    private static readonly (Regex Words, Vector3 Color)[] NamedColors =
    [
        (new("fire|flame|burn|blaze|explo|magma|lava|heat|ember", RegexOptions.IgnoreCase), new(1f, 0.55f, 0.18f)),
        (new("ice|frost|freez|snow|cold|aqua|water|bubble", RegexOptions.IgnoreCase), new(0.45f, 0.8f, 1f)),
        (new("thunder|electr|lightning|spark|volt|plasma", RegexOptions.IgnoreCase), new(1f, 0.92f, 0.45f)),
        (new("dark|demon|shadow|evil|curse|death|void|vice", RegexOptions.IgnoreCase), new(0.75f, 0.35f, 1f)),
        (new("poison|toxic|venom|acid|leaf|grass|nature|heal", RegexOptions.IgnoreCase), new(0.5f, 1f, 0.45f)),
        (new("holy|light|angel|heaven|gold|shine|sun", RegexOptions.IgnoreCase), new(1f, 0.95f, 0.7f)),
        (new("blood|red", RegexOptions.IgnoreCase), new(1f, 0.3f, 0.25f)),
        (new("wind|air|tornado|smoke|dust", RegexOptions.IgnoreCase), new(0.85f, 0.88f, 0.9f))
    ];

    private static readonly Regex TintName = new("^(color|colour|tint|emissive|base ?color|particle ?color|main ?color|fx ?color)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ViewerEffect Read(UObject system, ViewerLoadContext context)
    {
        // The system's package, plus the packages of emitters it references (UE4 Niagara keeps them apart)
        var exports = new List<UObject>();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPackage(UObject anyExport)
        {
            if (anyExport.Owner is not { } owner || !packages.Add(owner.Name)) return;
            exports.AddRange(owner.GetExports());
        }
        AddPackage(system);
        foreach (var emitter in exports.ToArray().SelectMany(e => NotifyReader.References(e, c => c.EndsWith("Emitter", StringComparison.Ordinal))))
            AddPackage(emitter);

        var systemSize = BoundsSize(system);
        var namedColor = NamedColors.FirstOrDefault(c => c.Words.IsMatch(system.Name)).Color;
        var layers = new List<EffectLayer>();
        foreach (var export in exports)
        {
            if (layers.Count >= MaxLayers) break;
            try
            {
                var layer = export.ExportType switch
                {
                    var c when c.EndsWith("SpriteRendererProperties", StringComparison.Ordinal) || c.EndsWith("RibbonRendererProperties", StringComparison.Ordinal) =>
                        Sprite(export, export.GetOrDefault<FPackageIndex?>("Material"), FlipbookOf(export), OuterSize(export) ?? systemSize, context),
                    var c when c.EndsWith("MeshRendererProperties", StringComparison.Ordinal) => MeshLayer(export, context, OuterSize(export) ?? systemSize),
                    "ParticleModuleRequired" => Sprite(export, export.GetOrDefault<FPackageIndex?>("Material"),
                        (Math.Max(1, export.GetOrDefault("SubImages_Horizontal", 1)), Math.Max(1, export.GetOrDefault("SubImages_Vertical", 1))),
                        systemSize, context),
                    _ => null
                };
                if (layer is null || Invisible.IsMatch(layer.Name)) continue;
                // A grayscale picture (or the soft glow) takes the effect's color when its material gives none
                if (layer.Tint == Vector3.One && namedColor != default && (layer.Texture is null || Saturation(layer.Texture) < 0.12f))
                    layer = layer with { Tint = namedColor };
                layers.Add(layer);
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Skipping an emitter of {Effect}", system.Name);
            }
        }
        return new ViewerEffect(system.Name, layers.ToArray());
    }

    private static (int Columns, int Rows) FlipbookOf(UObject renderer)
    {
        var size = renderer.GetOrDefault("SubImageSize", new FVector2D(1, 1));
        return (Math.Clamp((int) size.X, 1, 64), Math.Clamp((int) size.Y, 1, 64));
    }

    private static EffectLayer? MeshLayer(UObject renderer, ViewerLoadContext context, float fallbackSize)
    {
        // Niagara mesh renderers: Meshes[].Mesh, and OverrideMaterials[].ExplicitMat when set
        UStaticMesh? mesh = null;
        foreach (var entry in renderer.GetOrDefault<FStructFallback[]>("Meshes", []))
            if (entry.GetOrDefault<FPackageIndex?>("Mesh") is { } pointer && pointer.TryLoad(out mesh)) break;
        if (mesh is null && renderer.GetOrDefault<FPackageIndex?>("ParticleMesh") is { } single) single.TryLoad(out mesh);

        FPackageIndex? material = null;
        foreach (var entry in renderer.GetOrDefault<FStructFallback[]>("OverrideMaterials", []))
            if (entry.GetOrDefault<FPackageIndex?>("ExplicitMat") is { IsNull: false } explicitMaterial)
            {
                material = explicitMaterial;
                break;
            }
        var materialObject = material is { IsNull: false } ? material.Load<UMaterialInterface>() : mesh?.Materials.FirstOrDefault(m => m is not null)?.Load<UMaterialInterface>();

        var size = mesh?.RenderData?.Bounds is { SphereRadius: > 0 } bounds ? Math.Clamp(bounds.SphereRadius, 5, 250) : fallbackSize;
        var (texture, tint, round) = Look(materialObject, context);
        return new EffectLayer(materialObject?.Name ?? mesh?.Name ?? renderer.Name, texture, tint, 1, 1, size, round);
    }

    private static EffectLayer Sprite(UObject renderer, FPackageIndex? material, (int Columns, int Rows) flipbook, float size, ViewerLoadContext context)
    {
        var materialObject = material is { IsNull: false } ? material.Load<UMaterialInterface>() : null;
        var (texture, tint, round) = Look(materialObject, context);
        return new EffectLayer(materialObject?.Name ?? renderer.Name, texture, tint, flipbook.Columns, flipbook.Rows, size, round);
    }

    // The emitter (the renderer's outer) usually has fixed bounds, a hint of how big the effect is
    private static float? OuterSize(UObject renderer) =>
        renderer.Outer?.Object?.Value is { } emitter && BoundsOf(emitter) is { } box ? SizeOf(box) : null;

    private static float BoundsSize(UObject system) =>
        BoundsOf(system) is { } box ? SizeOf(box) : DefaultSize;

    private static float SizeOf(FBox box)
    {
        var extent = new Vector3(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z) / 2;
        var half = (extent.X + extent.Y + extent.Z) / 3;
        return half > 0 ? Math.Clamp(half * 0.6f, 8, 250) : DefaultSize;
    }

    // Niagara's FixedBounds (system or emitter, sometimes inside the versioned emitter data), Cascade's FixedRelativeBoundingBox
    private static FBox? BoundsOf(UObject obj)
    {
        foreach (var name in new[] { "FixedBounds", "FixedRelativeBoundingBox" })
            if (Find(obj.Properties, name, 0) is FBox box && box.Max.X > box.Min.X) return box;
        return null;
    }

    private static object? Find(IEnumerable<FPropertyTag> properties, string name, int depth)
    {
        if (depth > 4) return null;
        foreach (var property in properties)
        {
            var value = property.Tag?.GenericValue;
            if (property.Name.Text == name)
                return value is FScriptStruct { StructType: var structType } ? structType : value;
            var nested = value switch
            {
                FScriptStruct { StructType: FStructFallback fallback } => Find(fallback.Properties, name, depth + 1),
                UScriptArray array => array.Properties.Select(p => p.GenericValue).OfType<FScriptStruct>()
                    .Select(s => s.StructType).OfType<FStructFallback>().Select(f => Find(f.Properties, name, depth + 1)).FirstOrDefault(v => v is not null),
                _ => null
            };
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// The texture and color an effect material draws with: its texture parameters up the instance chain (then what
    /// the base material references), preferring names of pictures over noise, masks and distortion; the tint from a
    /// color parameter (HDR emissive colors brought back to 0–1). Null texture: a procedural shape (a glow).
    /// </summary>
    private static (ViewerTexture? Texture, Vector3 Tint, bool Round) Look(UMaterialInterface? material, ViewerLoadContext context)
    {
        var textures = new List<(string Name, UTexture Texture)>();
        Vector3? tint = null;
        for (var current = material; current is not null;)
        {
            if (current is UMaterialInstanceConstant constant)
            {
                foreach (var value in constant.TextureParameterValues)
                    if (value.ParameterValue?.TryLoad<UTexture>(out var texture) == true) textures.Add((value.Name, texture));
                foreach (var value in constant.VectorParameterValues)
                    if (tint is null && TintName.IsMatch(value.Name) && value.ParameterValue is { } linear) tint = new Vector3(linear.R, linear.G, linear.B);
            }
            if (current is UMaterialInstance instance)
            {
                current = instance.Parent?.Load<UMaterialInterface>();
                continue;
            }
            if (current is UMaterial root)
                foreach (var reference in root.ReferencedTextures)
                    if (reference?.TryLoad<UTexture>(out var texture) == true) textures.Add(("", texture));
            break;
        }

        var best = textures
            .Select((t, i) => (t.Texture, Score: Score($"{t.Name} {t.Texture.Name}") - i * 0.01f))
            .Where(t => t.Score > -2)
            .OrderByDescending(t => t.Score)
            .Select(t => t.Texture)
            .FirstOrDefault();

        var color = tint ?? Vector3.One;
        var max = Math.Max(color.X, Math.Max(color.Y, color.Z));
        color = max < 0.02f ? Vector3.One : max > 1 ? color / max : color;
        if (best is null || Placeholder.IsMatch(best.Name)) return (null, color, false);
        var picture = ViewerModel.Decoded(best, context, 256);
        if (picture is null || DarkShare(picture) > 0.05f) return (picture, color, false);

        // A color picture with no background of its own: its shape is in another texture (an opacity map), as the
        // alpha; without one, the sprite fades out round
        var opacity = textures
            .Where(t => t.Texture != best)
            .OrderByDescending(t => OpacityName.IsMatch($"{t.Name} {t.Texture.Name}"))
            .Select(t => ViewerModel.Decoded(t.Texture, context, 256))
            .OfType<ViewerTexture>()
            .FirstOrDefault(o => o.Width * picture.Height == o.Height * picture.Width && DarkShare(o) > 0.2f);
        return opacity is null ? (picture, color, true) : (WithAlpha(picture, opacity), color, false);
    }

    private static readonly Regex OpacityName = new("opac|alpha|_o$|mask|_a$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Share of the pixels that add nothing (black, or transparent), from a sample
    private static float DarkShare(ViewerTexture texture)
    {
        int dark = 0, total = 0;
        var rgba = texture.Rgba;
        for (var i = 0; i + 3 < rgba.Length; i += 4 * 7, total++)
            if (rgba[i + 3] < 16 || Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])) < 16) dark++;
        return total > 0 ? (float) dark / total : 0;
    }

    // The picture with the opacity map as its alpha (the map's own alpha when it has one, else its brightness)
    private static ViewerTexture WithAlpha(ViewerTexture picture, ViewerTexture opacity)
    {
        var hasAlpha = false;
        for (var i = 3; i < opacity.Rgba.Length && !hasAlpha; i += 4) hasAlpha = opacity.Rgba[i] < 250;
        var rgba = (byte[]) picture.Rgba.Clone();
        for (var y = 0; y < picture.Height; y++)
        for (var x = 0; x < picture.Width; x++)
        {
            var o = ((y * opacity.Height / picture.Height) * opacity.Width + x * opacity.Width / picture.Width) * 4;
            var alpha = hasAlpha ? opacity.Rgba[o + 3] : Math.Max(opacity.Rgba[o], Math.Max(opacity.Rgba[o + 1], opacity.Rgba[o + 2]));
            rgba[(y * picture.Width + x) * 4 + 3] = (byte) alpha;
        }
        return picture with { Name = $"{picture.Name}+{opacity.Name}", Rgba = rgba };
    }

    // Average color saturation (0 for a gray picture), weighted by brightness, from a sample of the pixels
    private static float Saturation(ViewerTexture texture)
    {
        double total = 0, weight = 0;
        var rgba = texture.Rgba;
        for (var i = 0; i + 3 < rgba.Length; i += 4 * 17)
        {
            float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            var max = Math.Max(r, Math.Max(g, b));
            if (max < 8) continue;
            total += (max - Math.Min(r, Math.Min(g, b))) / max * max;
            weight += max;
        }
        return weight > 0 ? (float) (total / weight) : 0;
    }

    private static float Score(string name) => (MainTexture.IsMatch(name) ? 2 : 0) - (SecondaryTexture.IsMatch(name) ? 3 : 0);
}
