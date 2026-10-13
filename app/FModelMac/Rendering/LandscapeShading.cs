using System.Numerics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Textures;

namespace FModelMac.Rendering;

/// <summary>
/// Colors a landscape component by its paint layers. Landscape materials blend one set of textures per layer
/// (grass, rock, dirt…) with the component's weightmaps, through a material graph we can't evaluate. So each layer
/// gets the average color of the diffuse texture whose name matches it (Grass_LayerInfo → T_Ground_Grass_D), and the
/// vertices' layer weights blend those colors into a small texture per component.
/// FModel's 3D viewer doesn't show landscapes at all.
/// </summary>
public static class LandscapeShading
{
    private static readonly Regex Words = new("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", RegexOptions.Compiled);
    private static readonly Regex NotDiffuse = new(@"(^|_)(N|S|M|R|H|ORM|ORH|ARM|MRA|RMA)$|normal|rough|metal|mask|height|weightmap|placeholder|specular|occlusion|opacity|emissive|displacement",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsDiffuse = new(@"(^|_)(D|BC|C|Base)$|albedo|diffuse|basecolor|color|colour",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Vector3 Untextured = new(0.42f, 0.47f, 0.36f);

    /// <summary>
    /// A texture with one texel per vertex of the component, the blend of its layers' colors, and the vertices' UVs
    /// into it (texel centers, so bilinear filtering interpolates between vertices like the weightmaps do).
    /// </summary>
    public static (ViewerTexture Texture, Vector3 Average, FMeshUVFloat[] Uvs)? Bake(ULandscapeComponent component, MeshLodDto<MeshVertex> lod,
        ViewerLoadContext context)
    {
        if (lod.VertexColors is not { Length: > 0 } layers || lod.ExtraUvs is not [{ } extents, ..]) return null;
        var material = Material(component);
        var names = layers.Select(l => l.Name).ToArray();
        var colors = names.Select(l => LayerColor(l, names, material, context)).ToArray();

        var size = component.ComponentSizeQuads + 1;
        var rgba = new byte[size * size * 4];
        var uvs = new FMeshUVFloat[lod.Vertices.Length];
        var average = Vector3.Zero;
        for (var v = 0; v < lod.Vertices.Length; v++)
        {
            // ExtraUvs[0] spans the component: vertex x / (quads + 1)
            var x = Math.Clamp((int) MathF.Round(extents[v].U * size), 0, size - 1);
            var y = Math.Clamp((int) MathF.Round(extents[v].V * size), 0, size - 1);
            uvs[v] = new FMeshUVFloat((x + 0.5f) / size, (y + 0.5f) / size);

            var sum = Vector3.Zero;
            float total = 0;
            for (var l = 0; l < layers.Length; l++)
            {
                var weight = layers[l].Colors[v].R / 255f;
                if (weight <= 0) continue;
                sum += colors[l] * weight;
                total += weight;
            }
            var color = total > 0 ? sum / total : Untextured;
            average += color;

            var o = (y * size + x) * 4;
            rgba[o] = (byte) Math.Clamp(color.X * 255, 0, 255);
            rgba[o + 1] = (byte) Math.Clamp(color.Y * 255, 0, 255);
            rgba[o + 2] = (byte) Math.Clamp(color.Z * 255, 0, 255);
            rgba[o + 3] = 255;
        }
        return (new ViewerTexture($"{component.Name} layers", size, size, rgba), average / Math.Max(lod.Vertices.Length, 1), uvs);
    }

    // The component's own instance (it carries the weightmaps), its override, or the landscape's material
    private static UMaterialInterface? Material(ULandscapeComponent component)
    {
        foreach (var ptr in component.GetOrDefault<FPackageIndex[]>("MaterialInstances", []))
            if (ptr.TryLoad<UMaterialInterface>(out var instance)) return instance;
        if (component.OverrideMaterial?.TryLoad<UMaterialInterface>(out var overridden) == true) return overridden;
        return component.Outer?.Load<CUE4Parse.UE4.Assets.Exports.Actor.ALandscapeProxy>()?.LandscapeMaterial?.Load<UMaterialInterface>();
    }

    private static Vector3 LayerColor(string layer, string[] allLayers, UMaterialInterface? material, ViewerLoadContext context) =>
        context.LayerColors.GetOrAdd($"{material?.GetPathName()}|{layer}", _ => new Lazy<Vector3>(() =>
        {
            var words = LayerWords(layer);
            var others = allLayers.Where(l => l != layer).SelectMany(LayerWords).Except(words, StringComparer.OrdinalIgnoreCase).ToArray();
            var best = DiffuseCandidates(material, context)
                .Select(c => (c.Texture, Score: Score(c.Words, words) - 0.5f * Score(c.Words, others)))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Select(c => c.Texture)
                .FirstOrDefault();
            return best is not null && AverageColor(best, context) is { } color ? color : Palette(words);
        })).Value;

    /// <summary>"Grass_LayerInfo" → Grass; "WetPuddle_LayerInfo_001" → Wet, Puddle.</summary>
    public static string[] LayerWords(string layer)
    {
        var name = Regex.Replace(layer, @"_?(LayerInfo|LayerI?nfo|Layer|LI)(_\d+)?$|_\d+$", "", RegexOptions.IgnoreCase);
        return Words.Matches(name).Select(m => m.Value).Where(w => !char.IsDigit(w[0])).ToArray();
    }

    private static float Score(string[] candidate, string[] words) =>
        words.Count(w => candidate.Any(c => c.Equals(w, StringComparison.OrdinalIgnoreCase)));

    internal sealed record Candidate(UTexture Texture, string[] Words);

    // The diffuse textures the material can use: parameters of every instance up the chain, then what the base
    // material references (layers left at their defaults); each with the words of its parameter and texture names
    private static IReadOnlyList<Candidate> DiffuseCandidates(UMaterialInterface? material, ViewerLoadContext context)
    {
        if (material is null) return [];
        return context.LandscapeTextures.GetOrAdd(material.GetPathName(), _ => new Lazy<IReadOnlyList<Candidate>>(() =>
        {
            var list = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(UTexture? texture, string? parameter)
            {
                if (texture is null || NotDiffuse.IsMatch(texture.Name) || !IsDiffuse.IsMatch(texture.Name) && (parameter is null || !IsDiffuse.IsMatch(parameter))) return;
                if (!seen.Add($"{parameter}|{texture.GetPathName()}")) return;
                var words = Words.Matches($"{parameter} {texture.Name}").Select(m => m.Value).ToArray();
                list.Add(new Candidate(texture, words));
            }

            for (var current = material; current is not null;)
            {
                if (current is UMaterialInstance instance)
                {
                    if (instance is UMaterialInstanceConstant constant)
                        foreach (var value in constant.TextureParameterValues)
                            if (value.ParameterValue?.TryLoad<UTexture>(out var texture) == true) Add(texture, value.Name);
                    current = instance.Parent?.Load<UMaterialInterface>();
                }
                else
                {
                    if (current is UMaterial root)
                        foreach (var reference in root.ReferencedTextures)
                            if (reference?.TryLoad<UTexture>(out var texture) == true) Add(texture, null);
                    break;
                }
            }
            return list;
        })).Value;
    }

    private static Vector3? AverageColor(UTexture texture, ViewerLoadContext context) =>
        context.TextureColors.GetOrAdd(texture.GetPathName(), _ => new Lazy<Vector3?>(() =>
        {
            try
            {
                if (texture.Decode(64) is not { } decoded) return null;
                using var bitmap = decoded.ToSkBitmap();
                double r = 0, g = 0, b = 0;
                var count = 0;
                for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    r += c.Red;
                    g += c.Green;
                    b += c.Blue;
                    count++;
                }
                return count == 0 ? null : new Vector3((float) (r / count / 255), (float) (g / count / 255), (float) (b / count / 255));
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not read landscape texture {Texture}", texture.GetPathName());
                return null;
            }
        })).Value;

    // No texture to go by: a color from the layer's name
    private static Vector3 Palette(string[] words)
    {
        var name = string.Join(" ", words).ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("snow") || name.Contains("ice") => new(0.86f, 0.88f, 0.9f),
            _ when name.Contains("sand") || name.Contains("beach") || name.Contains("desert") => new(0.76f, 0.69f, 0.5f),
            _ when name.Contains("rock") || name.Contains("cliff") || name.Contains("stone") || name.Contains("gravel") => new(0.5f, 0.49f, 0.47f),
            _ when name.Contains("dirt") || name.Contains("mud") || name.Contains("soil") || name.Contains("ground") || name.Contains("path") => new(0.45f, 0.36f, 0.26f),
            _ when name.Contains("grass") || name.Contains("moss") || name.Contains("jungle") || name.Contains("forest") || name.Contains("leaf") => new(0.33f, 0.45f, 0.22f),
            _ when name.Contains("water") || name.Contains("wet") || name.Contains("puddle") => new(0.3f, 0.36f, 0.4f),
            _ => Untextured
        };
    }
}
