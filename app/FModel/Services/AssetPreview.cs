using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using FModel.Rendering;
using FModel.Services.Audio;
using Newtonsoft.Json;

namespace FModel.Services;

/// <summary>An image of the preview: a texture (or one layer of a texture array), PNG-encoded.</summary>
/// <param name="NearestNeighbor">Pixel art (FModel's RenderNearestNeighbor): scale it up without smoothing.</param>
public sealed record PreviewImage(string Name, byte[] Png, bool NearestNeighbor = false);

/// <param name="Highlight">Name of the .xshd definition to use (Json, Ini, Xml, …) or null for plain text.</param>
/// <param name="Images">Every texture of the package (FModel's tab images), or the image file itself.</param>
/// <param name="Mesh">First static/skeletal mesh of the package, for the 3D viewer.</param>
/// <param name="Animation">First animation (sequence, montage, composite, blend space) of the package, to play in the 3D viewer.</param>
/// <param name="World">The map of a .umap, for the 3D viewer.</param>
/// <param name="Sounds">Sounds of the package or file, for the audio player.</param>
/// <param name="Skeleton">The skeleton of the package, shown as an empty mesh with "Handle Skeletons as Empty Meshes".</param>
/// <param name="Material">The material (or material instance) of the package, previewed on a cube.</param>
public sealed record PreviewResult(string Text, string? Highlight, IReadOnlyList<PreviewImage>? Images,
    UObject? Mesh = null, UAnimationAsset? Animation = null, UWorld? World = null,
    IReadOnlyList<ExtractedSound>? Sounds = null, IReadOnlyList<string>? SoundWarnings = null, USkeleton? Skeleton = null,
    string? Warning = null, UMaterialInterface? Material = null);

public static class AssetPreview
{
    private const int MaxTextChars = 32 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "gif", "webp"
    };

    /// <param name="lua">Decompile compiled Lua chunks with these options (null: show them as they are).</param>
    /// <param name="platform">The game's texture platform (Settings › General), for console cooks.</param>
    public static PreviewResult Build(IFileProvider provider, GameFile file, CancellationToken ct, SoundExtractor? audio = null,
        LuaOptions? lua = null, ETexturePlatform platform = ETexturePlatform.DesktopMobile)
    {
        // The extractor keeps per-call warnings: one extraction at a time
        (List<ExtractedSound> Sounds, string[] Warnings) Sounds(Func<SoundExtractor, List<ExtractedSound>> extract)
        {
            if (audio is null) return ([], []);
            lock (audio) return (extract(audio), audio.Warnings.ToArray());
        }

        if (file.IsUePackage)
        {
            var exports = LoadExports(provider, file, ct);
            var json = JsonConvert.SerializeObject(exports, Formatting.Indented);
            ct.ThrowIfCancellationRequested();

            var images = EncodeTextures(exports.OfType<UTexture>(), platform, ct, out var imageWarnings);
            var (sounds, warnings) = Sounds(a => a.FromExports(file, exports));
            return new PreviewResult(Truncate(json), "Json", images,
                exports.FirstOrDefault(ViewerModel.CanView),
                exports.OfType<UAnimationAsset>().FirstOrDefault(a => a is UAnimSequence or UAnimMontage or UAnimComposite or UBlendSpaceBase),
                exports.OfType<UWorld>().FirstOrDefault(), sounds, warnings, exports.OfType<USkeleton>().FirstOrDefault(),
                imageWarnings, exports.OfType<UMaterialInterface>().FirstOrDefault());
        }

        if (audio is not null && SoundExtractor.AudioExtensions.Contains(file.Extension))
        {
            string? bankJson = null;
            var (sounds, warnings) = Sounds(a => a.FromFile(file, out bankJson));
            var text = bankJson ?? $"{file.Name}: {file.Extension.ToUpperInvariant()} audio, {FormatSize(file.Size)}";
            return new PreviewResult(Truncate(text), bankJson is null ? null : "Json", null, Sounds: sounds, SoundWarnings: warnings);
        }

        var bytes = file.Read();
        ct.ThrowIfCancellationRequested();

        if (ImageExtensions.Contains(file.Extension))
            return new PreviewResult("", null, [new PreviewImage(file.NameWithoutExtension, bytes)]);

        // FModel's "Decompile Lua": compiled chunks through unluac; source .lua files are shown as they are
        if (LuaDecompiler.IsChunk(bytes))
        {
            if (lua is null) return new PreviewResult(HexDump(bytes, 64 * 1024), null, null, Warning: "Compiled Lua: turn on Settings › Lua › Decompile Lua to read it");
            var result = LuaDecompiler.Decompile(bytes, lua);
            if (result.Text is { } code)
                return new PreviewResult(Truncate(code), lua.Disassemble ? null : "Lua", null,
                    Warning: result.Partial ? $"{file.Name}: partially decompiled ({result.Error})" : null);
            return new PreviewResult(HexDump(bytes, 64 * 1024), null, null, Warning: $"{file.Name}: {result.Error}");
        }

        var highlight = HighlightFor(file.Extension);
        return highlight is not null || LooksLikeText(bytes) // e.g. .txt/.csv: text without highlighting
            ? new PreviewResult(Truncate(Encoding.UTF8.GetString(bytes)), highlight, null)
            : new PreviewResult(HexDump(bytes, 64 * 1024), null, null);
    }

    public static UObject[] LoadExports(IFileProvider provider, GameFile file, CancellationToken ct = default)
    {
        var exports = provider.LoadPackage(file).GetExports().ToArray();
        ct.ThrowIfCancellationRequested();
        return exports;
    }

    /// <summary>
    /// FModel's AddImage for each texture: every layer of a texture array (name_0, name_1…), cubemaps unwrapped as a
    /// panorama, the rest as they are. Textures that can't be decoded are skipped and reported.
    /// </summary>
    public static List<PreviewImage> EncodeTextures(IEnumerable<UTexture> textures, ETexturePlatform platform, CancellationToken ct,
        out string? warning)
    {
        var images = new List<PreviewImage>();
        var failed = new List<string>();
        foreach (var texture in textures)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var decoded = texture is UTexture2DArray array
                    ? array.DecodeTextureArray(platform) ?? []
                    : texture.Decode(platform) is { } single ? [texture is UTextureCube ? single.ToPanorama() : single] : [];
                if (decoded.Length == 0) failed.Add(texture.Name);
                for (var i = 0; i < decoded.Length; i++)
                    images.Add(new PreviewImage(decoded.Length > 1 || texture is UTexture2DArray ? $"{texture.Name}_{i}" : texture.Name,
                        decoded[i].Encode(ETextureFormat.Png, false, out _), texture.RenderNearestNeighbor));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Serilog.Log.Warning(e, "Could not decode {Texture}", texture.GetPathName());
                failed.Add(texture.Name);
            }
        }
        warning = failed.Count == 0 ? null : $"Could not decode {string.Join(", ", failed)}";
        return images;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.##} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.##} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    private static string? HighlightFor(string extension) => extension.ToLowerInvariant() switch
    {
        "json" or "uproject" or "uplugin" or "upluginmanifest" => "Json",
        "ini" or "cfg" => "Ini",
        "xml" or "html" or "xaml" => "Xml",
        "lua" or "luac" => "Lua",
        "verse" => "Verse",
        "usf" or "ush" or "h" or "cpp" or "hlsl" => "Cpp",
        _ => null
    };

    private static bool LooksLikeText(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        return sample.IndexOf((byte) 0) < 0;
    }

    private static string Truncate(string text) => text.Length <= MaxTextChars
        ? text
        : text[..MaxTextChars] + $"\n\n… truncated ({text.Length:N0} characters)";

    private static string HexDump(byte[] bytes, int max)
    {
        var sb = new StringBuilder();
        var count = Math.Min(bytes.Length, max);
        for (var offset = 0; offset < count; offset += 16)
        {
            var line = bytes.AsSpan(offset, Math.Min(16, count - offset));
            sb.Append($"{offset:X8}  ");
            for (var i = 0; i < 16; i++)
                sb.Append(i < line.Length ? $"{line[i]:X2} " : "   ");
            sb.Append(' ');
            foreach (var b in line)
                sb.Append(b is >= 0x20 and < 0x7F ? (char) b : '.');
            sb.AppendLine();
        }
        if (bytes.Length > max) sb.AppendLine($"… {bytes.Length - max:N0} more bytes");
        return sb.ToString();
    }
}
