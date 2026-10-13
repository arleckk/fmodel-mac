using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using FModelMac.Rendering;
using FModelMac.Services.Audio;
using Newtonsoft.Json;

namespace FModelMac.Services;

/// <param name="Highlight">Name of the .xshd definition to use (Json, Ini, Xml, …) or null for plain text.</param>
/// <param name="Mesh">First static/skeletal mesh of the package, for the 3D viewer.</param>
/// <param name="Animation">First animation (sequence, montage, composite, blend space) of the package, to play in the 3D viewer.</param>
/// <param name="World">The map of a .umap, for the 3D viewer.</param>
/// <param name="Sounds">Sounds of the package or file, for the audio player.</param>
public sealed record PreviewResult(string Text, string? Highlight, byte[]? Image, string? ImageName,
    UObject? Mesh = null, UAnimationAsset? Animation = null, UWorld? World = null,
    IReadOnlyList<ExtractedSound>? Sounds = null, IReadOnlyList<string>? SoundWarnings = null);

public static class AssetPreview
{
    private const int MaxTextChars = 32 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "gif", "webp"
    };

    public static PreviewResult Build(IFileProvider provider, GameFile file, CancellationToken ct, SoundExtractor? audio = null)
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

            var texture = exports.OfType<UTexture>().FirstOrDefault();
            var png = texture is null ? null : EncodeTexture(texture);
            var (sounds, warnings) = Sounds(a => a.FromExports(file, exports));
            return new PreviewResult(Truncate(json), "Json", png, png is null ? null : texture!.Name,
                exports.FirstOrDefault(ViewerModel.CanView),
                exports.OfType<UAnimationAsset>().FirstOrDefault(a => a is UAnimSequence or UAnimMontage or UAnimComposite or UBlendSpaceBase),
                exports.OfType<UWorld>().FirstOrDefault(), sounds, warnings);
        }

        if (audio is not null && SoundExtractor.AudioExtensions.Contains(file.Extension))
        {
            string? bankJson = null;
            var (sounds, warnings) = Sounds(a => a.FromFile(file, out bankJson));
            var text = bankJson ?? $"{file.Name}: {file.Extension.ToUpperInvariant()} audio, {FormatSize(file.Size)}";
            return new PreviewResult(Truncate(text), bankJson is null ? null : "Json", null, null, Sounds: sounds, SoundWarnings: warnings);
        }

        var bytes = file.Read();
        ct.ThrowIfCancellationRequested();

        if (ImageExtensions.Contains(file.Extension))
            return new PreviewResult("", null, bytes, file.Name);

        var highlight = HighlightFor(file.Extension);
        return highlight is not null || LooksLikeText(bytes) // e.g. .txt/.csv: text without highlighting
            ? new PreviewResult(Truncate(Encoding.UTF8.GetString(bytes)), highlight, null, null)
            : new PreviewResult(HexDump(bytes, 64 * 1024), null, null, null);
    }

    public static UObject[] LoadExports(IFileProvider provider, GameFile file, CancellationToken ct = default)
    {
        var exports = provider.LoadPackage(file).GetExports().ToArray();
        ct.ThrowIfCancellationRequested();
        return exports;
    }

    public static byte[]? EncodeTexture(UTexture texture) =>
        texture.Decode() is { } decoded ? decoded.Encode(ETextureFormat.Png, false, out _) : null;

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
