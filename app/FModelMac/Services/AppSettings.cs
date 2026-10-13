using System.Diagnostics;
using Newtonsoft.Json;
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

    // FModel's endpoint configuration: AES keys refreshed and mappings downloaded each time the game is mounted.
    // A local mapping file (UsmapPath) wins over the mapping endpoint, like FModel's "Overwrite".
    public EndpointConfig? AesEndpoint { get; set; }
    public EndpointConfig? MappingEndpoint { get; set; }
    /// <summary>Game directories that already got their FModel endpoint preset (applied once, then the user decides).</summary>
    public HashSet<string> EndpointPresetsApplied { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string MeshExportFormat { get; set; } = "Gltf2";
    public bool PreviewMeshes { get; set; } = true;
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
