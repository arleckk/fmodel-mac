using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;

namespace FModel.Headless;

/// <summary>What changed since a backup, as the command line and the MCP server report it.</summary>
public sealed record ChangesReport(string Backup, int BackupFiles, IReadOnlyList<string> New, IReadOnlyList<string> Modified, int Removed);

/// <summary>
/// Backups and "only what changed" without the window: FModel's Directory › Backup › Create, and its "All (New)" /
/// "All (Modified)" loading modes turned into a filter for exports. Plus the export choices a script wants per run
/// (several kinds at once, the mesh format) without touching the app's settings.
/// </summary>
public static class HeadlessChanges
{
    /// <summary>Every export kind but raw: "export everything" means converted files, not the package bytes again.</summary>
    public static readonly ExportKind[] All =
        [ExportKind.Properties, ExportKind.Textures, ExportKind.Models, ExportKind.Animations, ExportKind.Worlds, ExportKind.Audio, ExportKind.Code];

    /// <summary>"textures", "textures,models" or "all" (every kind but raw).</summary>
    public static IReadOnlyList<ExportKind> ParseKinds(string kinds) => kinds.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
        ? All
        : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(HeadlessExport.ParseKind).Distinct().ToArray();

    /// <summary>The backup a script means: a .fbkp path, or "latest" for the newest one in Output/Backups.</summary>
    public static string ResolveBackup(AppSettings settings, string backup)
    {
        if (backup.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return GameBackup.List(settings).FirstOrDefault()?.Path
                   ?? throw new HeadlessException($"No backups in {GameBackup.Directory(settings)}: create one with 'fmodel backup'");
        }
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(backup));
        return File.Exists(path) ? path : throw new HeadlessException($"No backup '{path}'");
    }

    /// <summary>FModel's "All (New)" and "All (Modified)" against <paramref name="backupPath"/>, sorted.</summary>
    public static ChangesReport Compare(HeadlessGame game, string backupPath)
    {
        var c = GameBackup.Compare(game.Archive.Provider, backupPath);
        return new ChangesReport(backupPath, c.BackupCount,
            c.New.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            c.Modified.Order(StringComparer.OrdinalIgnoreCase).ToArray(), c.Removed);
    }

    /// <summary>Keeps the files that are new or modified since the backup.</summary>
    public static IReadOnlyList<GameFile> OnlyChanged(IEnumerable<GameFile> files, ChangesReport changes)
    {
        var wanted = new HashSet<string>(changes.New, StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(changes.Modified);
        return files.Where(f => wanted.Contains(f.Path)).ToArray();
    }

    /// <summary>A backup of the mounted game, where FModel keeps them unless <paramref name="output"/> says otherwise.</summary>
    public static (string Path, int Count) CreateBackup(HeadlessGame game, string? output)
    {
        if (output is not null && output.EndsWith(".fbkp", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(output);
            var created = GameBackup.Create(game.Archive.Provider, game.Archive.ProjectName, Path.GetDirectoryName(path)!);
            if (!string.Equals(created.Path, path, StringComparison.Ordinal)) File.Move(created.Path, path, overwrite: true);
            return (path, created.Count);
        }
        var directory = output is null ? GameBackup.Directory(game.Settings) : Path.GetFullPath(output);
        return GameBackup.Create(game.Archive.Provider, game.Archive.ProjectName, directory);
    }

    private static readonly Dictionary<string, EMeshFormat> MeshFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gltf"] = EMeshFormat.Gltf2, ["gltf2"] = EMeshFormat.Gltf2, ["glb"] = EMeshFormat.Gltf2,
        ["uemodel"] = EMeshFormat.UEFormat, ["ueformat"] = EMeshFormat.UEFormat,
        ["usd"] = EMeshFormat.USD,
        ["actorx"] = EMeshFormat.ActorX, ["psk"] = EMeshFormat.ActorX
    };

    private static readonly Dictionary<string, EFileCompressionFormat> Compressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = EFileCompressionFormat.None, ["zstd"] = EFileCompressionFormat.ZSTD, ["gzip"] = EFileCompressionFormat.GZIP
    };

    public const string MeshFormatList = "gltf, uemodel, usd, actorx";
    public const string CompressionList = "none, zstd, gzip";

    /// <summary>
    /// Export settings for this run only. The loaded settings are a copy the command line and the MCP server never
    /// save, so the app keeps what Settings › Export says.
    /// </summary>
    public static void Override(AppSettings settings, string? meshFormat, string? compression)
    {
        if (meshFormat is not null)
        {
            settings.MeshExportFormat = MeshFormats.TryGetValue(meshFormat.Trim(), out var format)
                ? format
                : throw new HeadlessException($"Unknown mesh format '{meshFormat}': use one of {MeshFormatList}");
        }
        if (compression is not null)
        {
            settings.CompressionFormat = Compressions.TryGetValue(compression.Trim(), out var value)
                ? value
                : throw new HeadlessException($"Unknown compression '{compression}': use one of {CompressionList}");
        }
    }
}
