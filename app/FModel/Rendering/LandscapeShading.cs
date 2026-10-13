using System.Numerics;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Textures;

namespace FModel.Rendering;

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
    /// A texture with one texel per vertex of the component, the vertices' UVs into it (texel centers, so bilinear
    /// filtering interpolates between vertices like the weightmaps do), and the layers drawn with their own textures:
    /// the (up to) four heaviest layers that have a diffuse texture get their weights in <see cref="LandscapeLayers"/>,
    /// and the texture holds the blend of the other layers' colors, for the weight left over.
    /// </summary>
    public static (ViewerTexture Texture, Vector3 Average, FMeshUVFloat[] Uvs, LandscapeLayers? Layers)? Bake(ULandscapeComponent component,
        MeshLodDto<MeshVertex> lod, ViewerLoadContext context)
    {
        if (lod.VertexColors is not { Length: > 0 } layers || lod.ExtraUvs is not [{ } extents, ..]) return null;
        var material = Material(component);
        var names = layers.Select(l => l.Name).ToArray();
        var resolved = names.Select(l => Layer(l, names, material, context)).ToArray();
        var colors = resolved.Select(r => r.Color).ToArray();

        // The textured layers this component uses most
        var textured = Enumerable.Range(0, layers.Length)
            .Where(l => resolved[l].Texture is not null)
            .Select(l => (Layer: l, Total: layers[l].Colors.Sum(c => (long) c.R)))
            .Where(l => l.Total > 0)
            .OrderByDescending(l => l.Total)
            .Take(4).Select(l => l.Layer).ToArray();
        var slot = Enumerable.Repeat(-1, layers.Length).ToArray();
        for (var i = 0; i < textured.Length; i++) slot[textured[i]] = i;

        var size = component.ComponentSizeQuads + 1;
        var rgba = new byte[size * size * 4];
        var weights = new byte[size * size * 4];
        var uvs = new FMeshUVFloat[lod.Vertices.Length];
        var average = Vector3.Zero;
        for (var v = 0; v < lod.Vertices.Length; v++)
        {
            // ExtraUvs[0] spans the component: vertex x / (quads + 1)
            var x = Math.Clamp((int) MathF.Round(extents[v].U * size), 0, size - 1);
            var y = Math.Clamp((int) MathF.Round(extents[v].V * size), 0, size - 1);
            uvs[v] = new FMeshUVFloat((x + 0.5f) / size, (y + 0.5f) / size);

            Vector3 sum = Vector3.Zero, rest = Vector3.Zero;
            float total = 0, restTotal = 0;
            for (var l = 0; l < layers.Length; l++)
            {
                var weight = layers[l].Colors[v].R / 255f;
                if (weight <= 0) continue;
                sum += colors[l] * weight;
                total += weight;
                if (slot[l] >= 0) continue;
                rest += colors[l] * weight;
                restTotal += weight;
            }
            var color = total > 0 ? sum / total : Untextured;
            average += color;

            var o = (y * size + x) * 4;
            for (var l = 0; l < layers.Length; l++)
                if (slot[l] >= 0 && total > 0)
                    weights[o + slot[l]] = (byte) Math.Clamp(layers[l].Colors[v].R / 255f / total * 255 + 0.5f, 0, 255);
            var shown = restTotal > 0 ? rest / restTotal : color; // what the textured layers leave over
            rgba[o] = (byte) Math.Clamp(shown.X * 255, 0, 255);
            rgba[o + 1] = (byte) Math.Clamp(shown.Y * 255, 0, 255);
            rgba[o + 2] = (byte) Math.Clamp(shown.Z * 255, 0, 255);
            rgba[o + 3] = 255;
        }

        LandscapeLayers? textures = null;
        if (textured.Length > 0)
        {
            var tiling = new float[4];
            var decoded = new ViewerTexture?[4];
            for (var i = 0; i < textured.Length; i++)
            {
                decoded[i] = ViewerModel.Decoded(resolved[textured[i]].Texture, context, context.MaxTextureSize);
                tiling[i] = decoded[i] is null ? 0 : resolved[textured[i]].Tiling; // the shader shows the color blend instead
            }
            textures = new LandscapeLayers(new ViewerTexture($"{component.Name} weights", size, size, weights), decoded,
                new Vector4(tiling[0], tiling[1], tiling[2], tiling[3]),
                textured.Select(l => $"{string.Join("", LayerWords(names[l]))} ({resolved[l].Texture!.Name}, {resolved[l].Tiling:0.##} m)").ToArray());
        }
        return (new ViewerTexture($"{component.Name} layers", size, size, rgba), average / Math.Max(lod.Vertices.Length, 1), uvs, textures);
    }

    // The component's own instance (it carries the weightmaps), its override, or the landscape's material
    private static UMaterialInterface? Material(ULandscapeComponent component)
    {
        foreach (var ptr in component.GetOrDefault<FPackageIndex[]>("MaterialInstances", []))
            if (ptr.TryLoad<UMaterialInterface>(out var instance)) return instance;
        if (component.OverrideMaterial?.TryLoad<UMaterialInterface>(out var overridden) == true) return overridden;
        return component.Outer?.Load<CUE4Parse.UE4.Assets.Exports.Actor.ALandscapeProxy>()?.LandscapeMaterial?.Load<UMaterialInterface>();
    }

    /// <summary>A paint layer as drawn: its diffuse texture (if one matches), color, and how many quads one repeat spans.</summary>
    internal sealed record ResolvedLayer(UTexture? Texture, Vector3 Color, float Tiling);

    private const float DefaultTiling = 8; // quads (1 m each at the usual landscape scale) per repeat

    private static ResolvedLayer Layer(string layer, string[] allLayers, UMaterialInterface? material, ViewerLoadContext context) =>
        context.LayerColors.GetOrAdd($"{material?.GetPathName()}|{layer}", _ => new Lazy<ResolvedLayer>(() =>
        {
            var words = LayerWords(layer);
            var others = allLayers.Where(l => l != layer).SelectMany(LayerWords).Except(words, StringComparer.OrdinalIgnoreCase).ToArray();
            var best = DiffuseCandidates(material, context)
                .Select(c => (c.Texture, Score: Score(c.Words, words) - 0.5f * Score(c.Words, others)))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .Select(c => c.Texture)
                .FirstOrDefault();
            var color = best is not null && AverageColor(best, context) is { } average ? average : Palette(words);
            return new ResolvedLayer(best is not null && AverageColor(best, context) is not null ? best : null, color, Tiling(words, material));
        })).Value;

    // The layer's tiling scalar ("Cliff_TilingSize", "Ground_Tiling", "Jungle_TileSize", "grass_UVScale"…), from the
    // instances up the chain. Big values are the size of one repeat in quads (LandscapeCoords' mapping scale); small
    // ones a UV multiplier, so 0.1 repeats every 10 quads
    private static readonly Regex TilingName = new("til|uv|scale|size", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static float Tiling(string[] words, UMaterialInterface? material)
    {
        var best = (Score: 0f, Value: 0f);
        for (var current = material; current is UMaterialInstance instance; current = instance.Parent?.Load<UMaterialInterface>())
        {
            if (instance is not UMaterialInstanceConstant constant) continue;
            foreach (var parameter in constant.ScalarParameterValues)
            {
                var name = parameter.Name;
                if (name is null || !TilingName.IsMatch(name) || parameter.ParameterValue <= 0) continue;
                var score = Score(Words.Matches(name).Select(m => m.Value).ToArray(), words);
                if (score > best.Score) best = (score, parameter.ParameterValue);
            }
        }
        if (best.Score <= 0) return DefaultTiling;
        var tiling = best.Value >= 1 ? best.Value : 1 / best.Value;
        return Math.Clamp(tiling, 0.5f, 200);
    }

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
