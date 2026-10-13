using System.Diagnostics;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Serilog;

namespace FModelMac.Services;

// macOS replacements for the Windows-specific bits of FModel (AppData paths, explorer.exe /select)
public sealed class AppSettings
{
    // FMODELMAC_DATA_DIR lets tools like the snapshot harness keep their settings away from the user's
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("FMODELMAC_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModelMac");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public string? ArchiveDir { get; set; }
    public string Game { get; set; } = "GAME_UE5_LATEST";
    public string? AesKey { get; set; }
    public string? UsmapPath { get; set; }
    public string OutputDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FModelMac", "Output");
    public Dictionary<string, string> DynamicKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LoadingMode { get; set; } = "All";

    /// <summary>FModel's "Packages Language": the culture localized texts (FText) are read in.</summary>
    public string AssetLanguage { get; set; } = nameof(ELanguage.English);

    [JsonIgnore]
    public ELanguage AssetLanguageValue => Enum.TryParse<ELanguage>(AssetLanguage, out var value) ? value : ELanguage.English;

    /// <summary>Versioning configuration per game directory, like FModel's per-directory settings.</summary>
    public Dictionary<string, VersioningSettings> Versioning { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The versioning of the current game directory (created on demand).</summary>
    [JsonIgnore]
    public VersioningSettings CurrentVersioning
    {
        get
        {
            var key = ArchiveDir ?? "";
            if (!Versioning.TryGetValue(key, out var versioning)) Versioning[key] = versioning = new VersioningSettings();
            return versioning;
        }
    }

    // FModel's endpoint configuration: AES keys refreshed and mappings downloaded each time the game is mounted.
    // A local mapping file (UsmapPath) wins over the mapping endpoint, like FModel's "Overwrite".
    public EndpointConfig? AesEndpoint { get; set; }
    public EndpointConfig? MappingEndpoint { get; set; }
    /// <summary>Game directories that already got their FModel endpoint preset (applied once, then the user decides).</summary>
    public HashSet<string> EndpointPresetsApplied { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // FModel's export options (Settings › Export), passed to CUE4Parse's ExportSession. Defaults are FModel's,
    // except the mesh format: glTF opens anywhere, UEFormat and ActorX need a Blender add-on.
    [JsonConverter(typeof(StringEnumConverter))]
    public EMeshFormat MeshExportFormat { get; set; } = EMeshFormat.Gltf2;
    [JsonConverter(typeof(StringEnumConverter))]
    public EMeshQuality MeshQuality { get; set; } = EMeshQuality.Highest;
    [JsonConverter(typeof(StringEnumConverter))]
    public ESocketFormat SocketExportFormat { get; set; } = ESocketFormat.Bone;
    [JsonConverter(typeof(StringEnumConverter))]
    public EFileCompressionFormat CompressionFormat { get; set; } = EFileCompressionFormat.ZSTD;
    [JsonConverter(typeof(StringEnumConverter))]
    public ENaniteMeshFormat NaniteMeshExportFormat { get; set; } = ENaniteMeshFormat.NaniteOnly;
    public bool SaveMorphTargets { get; set; } = true;
    [JsonConverter(typeof(StringEnumConverter))]
    public ETextureFormat TextureExportFormat { get; set; } = ETextureFormat.Png;
    public int TextureQuality { get; set; } = 100;
    public bool SaveHdrTexturesAsHdr { get; set; } = true;
    public bool ExportAllTextureMips { get; set; }
    [JsonConverter(typeof(StringEnumConverter))]
    public EMaterialDepth MaterialExportFormat { get; set; } = EMaterialDepth.TopLayerOnly;
    public bool SaveEmbeddedMaterials { get; set; } = true;

    /// <summary>FModel's GetExportOptions; the texture platform comes from the game's versioning.</summary>
    public ExportOptions CreateExportOptions(EMeshFormat? meshFormat = null, ENaniteMeshFormat? naniteFormat = null) => new(
        meshFormat ?? MeshExportFormat, naniteFormat ?? NaniteMeshExportFormat, MeshQuality, CurrentVersioning.PlatformValue,
        TextureExportFormat, TextureQuality, SaveHdrTexturesAsHdr, ExportAllTextureMips,
        MaterialExportFormat, SaveEmbeddedMaterials, SaveMorphTargets, SocketExportFormat, CompressionFormat);

    public bool PreviewMeshes { get; set; } = true;
    // FModel's "Serialize Script Bytecode" / "Serialize Inlined Shader Maps": extra data in the properties JSON
    public bool ReadScriptData { get; set; }
    public bool ReadShaderMaps { get; set; }
    // Audio (FModel's Audio Player and audio export settings)
    public bool PlayAudioOnExtract { get; set; } = true;
    public bool ConvertAudioToWav { get; set; } = true;
    public double AudioVolume { get; set; } = 50;
    public string? AudioDevice { get; set; }
    public string? CriwareKey { get; set; }

    /// <summary>CRIWARE HCA/ADX decryption key, as typed (decimal or 0x hex); 0 when unset or invalid.</summary>
    [JsonIgnore]
    public ulong CriwareKeyValue
    {
        get
        {
            var key = CriwareKey?.Trim() ?? "";
            if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : 0;
            return ulong.TryParse(key, out var value) ? value : 0;
        }
    }

    public bool IsLoggerExpanded { get; set; } = true;
    public double ImagePanelWidth { get; set; } = 360;

    // FModel saves properties and textures under Output/Exports, mirroring the package path
    [JsonIgnore]
    public string ExportsDir => Path.Combine(OutputDir, "Exports");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read settings, using defaults");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    public static void RevealInFinder(string path)
    {
        var args = File.Exists(path) ? new[] { "-R", path } : [path];
        Process.Start(new ProcessStartInfo("open", args) { UseShellExecute = false });
    }
}

/// <summary>
/// What CUE4Parse needs beyond the engine version to read a game correctly: the texture platform, custom versions
/// for games that never serialize them, and overrides of its per-game options and map struct types.
/// </summary>
public sealed class VersioningSettings
{
    public string Platform { get; set; } = nameof(ETexturePlatform.DesktopMobile);
    public List<FCustomVersion>? CustomVersions { get; set; }
    public Dictionary<string, bool>? Options { get; set; }
    public Dictionary<string, KeyValuePair<string, string>>? MapStructTypes { get; set; }

    [JsonIgnore]
    public ETexturePlatform PlatformValue => Enum.TryParse<ETexturePlatform>(Platform, out var value) ? value : ETexturePlatform.DesktopMobile;

    [JsonIgnore]
    public bool HasOverrides => CustomVersions is { Count: > 0 } || Options is { Count: > 0 } || MapStructTypes is { Count: > 0 };

    public VersionContainer CreateContainer(EGame game) => new(game, PlatformValue,
        customVersions: CustomVersions is { Count: > 0 } ? new FCustomVersionContainer(CustomVersions) : null,
        optionOverrides: Options is { Count: > 0 } ? Options : null,
        mapStructTypesOverrides: MapStructTypes is { Count: > 0 } ? MapStructTypes : null);

    public VersioningSettings Clone() => new()
    {
        Platform = Platform,
        CustomVersions = CustomVersions?.ToList(),
        Options = Options is null ? null : new Dictionary<string, bool>(Options),
        MapStructTypes = MapStructTypes is null ? null : new Dictionary<string, KeyValuePair<string, string>>(MapStructTypes)
    };
}
