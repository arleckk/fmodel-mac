using System.ComponentModel;
using System.Reflection;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace FModel.Headless;

/// <summary>
/// FModel as an MCP server (<c>fmodel mcp</c>, over stdio): AI assistants browse a game with the app's profiles and
/// settings, read its packages as FModel's JSON, look at its textures and export assets. The game stays mounted
/// between calls; long texts come in pages.
/// </summary>
public sealed class FModelMcpServer
{
    private const string Instructions = """
        FModel reads the archives of Unreal Engine games (.pak, .utoc) with the profiles saved in the FModel app.
        Start with game_info: it mounts the default game (the app's active profile) and summarizes it; list_profiles and
        load_game switch games. Find assets with search_files and list_folder; read them with read_file (a package's
        exports as FModel's JSON: classes, properties and references to other packages), get_package_metadata,
        decompile_blueprint and find_references; look at textures with get_texture; export_assets writes files
        (glTF models, PNG textures, WAV sounds, JSON…) to the app's output folder.
        Paths are game paths ('Game/Content/Characters/Hero.uasset') or package paths ('/Game/Characters/Hero').
        Long texts come in pages: when a result ends with a [truncated …] note, call again with the offset it gives.
        """;

    private readonly GameTarget _target;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private Task<HeadlessGame>? _loading;
    private HeadlessGame? _game;

    private FModelMcpServer(GameTarget target) => _target = target;

    public static async Task<int> RunAsync(GameTarget target, CancellationToken ct)
    {
        var server = new FModelMcpServer(target);
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(FModelMcpServer).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            tools.Add(McpServerTool.Create(method, server));

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "fmodel",
                Title = "FModel",
                Version = typeof(FModelMcpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                WebsiteUrl = AppSettings.ProjectLink
            },
            ServerInstructions = Instructions,
            ToolCollection = tools
        };

        // Anything written to stdout by mistake would break the protocol: the transport keeps the real stdout stream
        var transport = new StdioServerTransport("fmodel");
        Console.SetOut(Console.Error);

        // Mount the default game while the client starts up, so the first call doesn't wait as long
        if (HasDefaultGame(target)) _ = server.GameAsync(ct);

        await using (transport)
        await using (var mcp = McpServer.Create(transport, options))
        {
            try
            {
                await mcp.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
        server._game?.Dispose();
        return 0;
    }

    private static bool HasDefaultGame(GameTarget target) =>
        !string.IsNullOrWhiteSpace(target.Directory) || HeadlessGame.Profiles() is { Count: > 0 } profiles &&
        (target.Profile is not null || profiles.Any(p => p.Active));

    // The mounted game, mounting the default one the first time (failures are retried on the next call)
    private async Task<HeadlessGame> GameAsync(CancellationToken ct)
    {
        if (_game is { } game) return game;
        await _gate.WaitAsync(ct);
        try
        {
            if (_game is not null) return _game;
            _loading ??= HeadlessGame.LoadAsync(_target, m => Serilog.Log.Information("{Message}", m), CancellationToken.None);
            try
            {
                return _game = await _loading.WaitAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _loading = null;
                throw;
            }
        }
        catch (HeadlessException e)
        {
            throw new McpException(e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- Games ----------

    [McpServerTool(Name = "list_profiles", Title = "List game profiles", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the game profiles saved in the FModel app: name, UE version and game directory. The active one (marked) is the default game.")]
    public string ListProfiles()
    {
        var profiles = HeadlessGame.Profiles();
        if (profiles.Count == 0) return "No saved profiles. Create one in the FModel app (Directory › Profiles), or call load_game with a directory.";
        var current = _game?.Settings.CurrentProfile;
        return string.Join('\n', profiles.Select(p =>
            $"{p.Name} | {p.Game} | {p.Directory}{(p.Active ? " | active in the app" : "")}{(p.Name == current ? " | mounted" : "")}" +
            $"{(p.HasAesKey || p.DynamicKeys > 0 ? "" : " | no AES key")}"));
    }

    [McpServerTool(Name = "load_game", Title = "Mount a game", ReadOnly = true, OpenWorld = false)]
    [Description("Mounts a game, replacing the current one: a saved profile by name, or a game directory (its Paks folder) with its UE version, AES key and mappings. Returns the game's summary.")]
    public async Task<string> LoadGame(
        [Description("A profile name from list_profiles.")] string? profile = null,
        [Description("A game's Paks folder, instead of a profile.")] string? directory = null,
        [Description("UE version as FModel names it, e.g. GAME_UE5_4 or GAME_UE4_27 (default: the profile's, or GAME_UE5_LATEST).")] string? ue_version = null,
        [Description("Main AES key, 64 hex characters (default: the profile's).")] string? aes_key = null,
        [Description("Path to a .usmap or .jmap mappings file (default: the profile's).")] string? mappings = null,
        CancellationToken ct = default)
    {
        if (profile is null && directory is null) throw new McpException("Give a profile (see list_profiles) or a directory");
        var target = new GameTarget(profile, directory, ue_version, aes_key, mappings);
        await _gate.WaitAsync(ct);
        try
        {
            var log = new List<string>();
            var game = await HeadlessGame.LoadAsync(target, log.Add, ct);
            var old = _game;
            _game = game;
            _loading = Task.FromResult(game);
            lock (_cacheLock) _cache.Clear();
            old?.Dispose();
            return Describe(game) + (log.Count > 0 ? "\n\nLog:\n" + string.Join('\n', log) : "");
        }
        catch (HeadlessException e)
        {
            throw new McpException(e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    [McpServerTool(Name = "game_info", Title = "Game summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Summarizes the mounted game (mounting the default one first): project, UE version, archives, file count, locked archives, mappings, and its top-level folders.")]
    public async Task<string> GameInfo(CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var root = game.Index.Folders[""];
        return Describe(game) + "\nTop-level folders: " + string.Join(", ", root.SubFolders.Select(f => f + "/"));
    }

    private static string Describe(HeadlessGame game)
    {
        var s = game.Summary();
        return $"""
            Game: {s.Name} ({s.Project}, {s.Game})
            Directory: {s.Directory}
            Archives: {s.Mounted}/{s.Archives} mounted{(s.Locked.Count > 0 ? $"; locked (need an AES key): {string.Join(", ", s.Locked.Take(10))}" : "")}
            Files: {s.Files:N0}
            Mappings: {s.Mappings ?? "none"}
            Language: {s.Language}
            """;
    }

    // ---------- Finding files ----------

    [McpServerTool(Name = "list_folder", Title = "List a folder", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists a folder of the game: its subfolders (ending in /), then its files with their sizes.")]
    public async Task<string> ListFolder(
        [Description("Folder path, e.g. 'Game/Content/Characters' or '/Game/Characters'. Empty for the root.")] string path = "",
        [Description("Entries to skip, for paging.")] int offset = 0,
        [Description("Most entries to return.")] int limit = 500,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var folder = game.FindFolder(path) ?? throw new McpException($"No folder '{path}'. Folders at the root: " +
                                                                     string.Join(", ", game.Index.Folders[""].SubFolders));
        var info = game.Index.Folders[folder];
        var prefix = folder.Length == 0 ? "" : folder + "/";
        var entries = info.SubFolders.Select(sub => $"{prefix}{sub}/")
            .Concat(info.Files.Select(f => $"{f.Path} ({AssetPreview.FormatSize(f.Size)})")).ToArray();
        return PageLines(entries, offset, limit, $"{folder}/ has {info.SubFolders.Count} folder(s) and {info.Files.Count} file(s)");
    }

    [McpServerTool(Name = "search_files", Title = "Search files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds files by path. Plain text matches anywhere in the path (case-insensitive); * and ? are wildcards over the file name (over the whole path when the pattern has a /); regex=true takes a regular expression over the path.")]
    public async Task<string> SearchFiles(
        [Description("Text, wildcard pattern (e.g. 'T_*_D.uasset') or regular expression.")] string query,
        [Description("Only search below this folder.")] string? folder = null,
        [Description("Treat query as a regular expression.")] bool regex = false,
        [Description("Results to skip, for paging.")] int offset = 0,
        [Description("Most results to return.")] int limit = 200,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        string? scope = null;
        if (!string.IsNullOrWhiteSpace(folder)) scope = game.FindFolder(folder) ?? throw new McpException($"No folder '{folder}'");
        var found = await Task.Run(() => Wrap(() => game.Search(query, scope, regex, 0, out _)), ct);
        return PageLines(found.Select(f => f.Path).ToArray(), offset, limit, $"{found.Count:N0} match(es)");
    }

    // ---------- Reading files ----------

    [McpServerTool(Name = "read_file", Title = "Read a file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads a file as FModel shows it: a package's exports as JSON (each with its Type, Name and Properties; references to other assets appear as ObjectPath values), text files as they are, compiled Lua decompiled, other binaries as a hex dump. Paged.")]
    public async Task<string> ReadFile(
        [Description("Game path or package path of the file.")] string path,
        [Description("Only this export of the package (by name).")] string? export_name = null,
        [Description("Character to start at, for the next page.")] int offset = 0,
        [Description("Most characters to return.")] int max_chars = 40_000,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var file = Resolve(game, path);
        var text = await Cached($"show|{file.Path}|{export_name}", () => game.Show(file, export_name, ct), ct);
        return Page(text, offset, max_chars);
    }

    [McpServerTool(Name = "get_package_metadata", Title = "Package metadata", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A package's summary, name map, imports (the packages and classes it depends on) and export map, as JSON. Paged.")]
    public async Task<string> GetPackageMetadata(
        [Description("Game path or package path of the package.")] string path,
        [Description("Character to start at, for the next page.")] int offset = 0,
        [Description("Most characters to return.")] int max_chars = 40_000,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var file = Package(game, path);
        var text = await Cached($"metadata|{file.Path}", () => game.Archive.PackageMetadata(file), ct);
        return Page(text, offset, max_chars);
    }

    [McpServerTool(Name = "decompile_blueprint", Title = "Decompile a Blueprint", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The Blueprint classes of a package as C++-like pseudocode: variables, functions and their logic. Paged.")]
    public async Task<string> DecompileBlueprint(
        [Description("Game path or package path of the Blueprint package.")] string path,
        [Description("Character to start at, for the next page.")] int offset = 0,
        [Description("Most characters to return.")] int max_chars = 40_000,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var file = Package(game, path);
        var text = await Cached($"blueprint|{file.Path}", () => game.Archive.DecompileBlueprint(file) ?? throw new McpException($"{file.Name} has no Blueprint class"), ct);
        return Page(text, offset, max_chars);
    }

    [McpServerTool(Name = "find_references", Title = "Find references", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the packages that reference (import) a package. The first call on a .pak game indexes every package once, which can take a minute; the index is kept on disk.")]
    public async Task<string> FindReferences(
        [Description("Game path or package path of the package.")] string path,
        [Description("Results to skip, for paging.")] int offset = 0,
        [Description("Most results to return.")] int limit = 500,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var file = Package(game, path);
        var report = progress is null ? null : new Progress<double>(p => progress.Report(new ProgressNotificationValue { Progress = (float) p, Total = 1, Message = "Indexing package imports" }));
        var refs = await Task.Run(() => game.Archive.FindReferences(file, report, ct), ct);
        return refs.Count == 0 ? $"Nothing references {file.Path}" : PageLines(refs.Select(f => f.Path).ToArray(), offset, limit, $"{refs.Count} package(s) reference {file.Name}");
    }

    [McpServerTool(Name = "get_texture", Title = "Look at textures", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the textures of a package (or an image file) as PNG images, scaled down to max_size: each layer of a texture array, cubemaps unwrapped.")]
    public async Task<IEnumerable<ContentBlock>> GetTexture(
        [Description("Game path or package path of the texture package or image.")] string path,
        [Description("Largest side, in pixels, of the returned images (16-2048).")] int max_size = 1024,
        [Description("Most images to return (texture arrays can have many layers).")] int max_images = 4,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var file = Resolve(game, path);
        string? warning = null;
        var images = await Task.Run(() => Wrap(() => game.Textures(file, out warning, ct)), ct);
        if (images.Count == 0) throw new McpException($"{file.Name} has no texture" + (warning is null ? "" : $" ({warning})"));
        var size = Math.Clamp(max_size, 16, 2048);
        var blocks = new List<ContentBlock>();
        var shown = images.Take(Math.Max(1, max_images)).ToArray();
        var notes = new StringBuilder();
        foreach (var image in shown)
        {
            var (png, width, height, scaled) = Scaled(image.Png, size);
            notes.AppendLine($"{image.Name}: {width}×{height}{(scaled is { } s ? $" (shown at {s.Width}×{s.Height})" : "")}");
            blocks.Add(ImageContentBlock.FromBytes(png, "image/png"));
        }
        if (images.Count > shown.Length) notes.AppendLine($"{images.Count - shown.Length} more image(s): raise max_images to see them");
        if (warning is not null) notes.AppendLine(warning);
        blocks.Insert(0, new TextContentBlock { Text = notes.ToString().TrimEnd() });
        return blocks;
    }

    // ---------- Exporting ----------

    [McpServerTool(Name = "export_assets", Title = "Export assets", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Exports files or whole folders to disk with the FModel app's export settings (glTF/UEFormat models, PNG textures, WAV sounds…), into its output folder (~/Documents/FModel/Output/Exports by default, keeping the game's folder structure). Returns the files written.")]
    public async Task<string> ExportAssets(
        [Description("Game paths or package paths of files or folders.")] string[] paths,
        [Description("What to export: properties (JSON), raw (the package files as they are), textures, models, animations, worlds, audio or code (decompiled Blueprints).")] string kind,
        [Description("Folder to write to, instead of the app's output folder.")] string? output_directory = null,
        CancellationToken ct = default)
    {
        var game = await GameAsync(ct);
        var exportKind = Wrap(() => HeadlessExport.ParseKind(kind));
        var files = new List<GameFile>();
        foreach (var path in paths)
        {
            if (game.FindFolder(path) is { } folder) files.AddRange(game.FilesUnder(folder));
            else files.Add(Resolve(game, path));
        }
        if (files.Count == 0) throw new McpException("Nothing to export: the folders are empty");

        // One export at a time: the output folder is a setting, and the Export Session's logs aren't per run
        var settings = game.Settings;
        await _exportGate.WaitAsync(ct);
        var outputDir = settings.OutputDir;
        ExportReport report;
        try
        {
            if (!string.IsNullOrWhiteSpace(output_directory)) settings.OutputDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(output_directory));
            report = await HeadlessExport.RunAsync(game, files.DistinctBy(f => f.Path).ToArray(), exportKind, null, ct);
        }
        finally
        {
            settings.OutputDir = outputDir;
            _exportGate.Release();
        }

        var text = new StringBuilder($"Exported {report.Files.Count} file(s) to {report.Directory}");
        if (report.Empty > 0) text.Append($"; {report.Empty} file(s) had no {kind}");
        text.AppendLine();
        foreach (var file in report.Files.Take(200)) text.AppendLine(file);
        if (report.Files.Count > 200) text.AppendLine($"… and {report.Files.Count - 200} more");
        if (report.Errors.Count > 0)
        {
            text.AppendLine($"{report.Errors.Count} error(s):");
            foreach (var error in report.Errors.Take(20)) text.AppendLine(error);
        }
        return text.ToString().TrimEnd();
    }

    // ---------- Helpers ----------

    private static GameFile Resolve(HeadlessGame game, string path) => Wrap(() => game.Resolve(path));

    private static GameFile Package(HeadlessGame game, string path)
    {
        var file = Resolve(game, path);
        return file.IsUePackage ? file : throw new McpException($"'{file.Path}' isn't a package (.uasset/.umap)");
    }

    // Errors meant for the user reach the model with their message (other exceptions are reported without it)
    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (HeadlessException e)
        {
            throw new McpException(e.Message);
        }
    }

    // The last texts read in this game, so asking for their next pages doesn't read and serialize the package again
    private readonly Lock _cacheLock = new();
    private readonly LinkedList<(string Key, string Text)> _cache = [];
    private const int CachedTexts = 8;

    private async Task<string> Cached(string key, Func<string> build, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cache.FirstOrDefault(c => c.Key == key) is { Text: not null } hit) return hit.Text;
        }
        var text = await Task.Run(() => Wrap(build), ct);
        lock (_cacheLock)
        {
            _cache.AddFirst((key, text));
            while (_cache.Count > CachedTexts) _cache.RemoveLast();
        }
        return text;
    }

    private static string Page(string text, int offset, int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 1_000, 200_000);
        offset = Math.Max(0, offset);
        if (offset >= text.Length && text.Length > 0) return $"[offset {offset} is past the end: the text has {text.Length:N0} characters]";
        var end = Math.Min(text.Length, offset + maxChars);
        var chunk = text[offset..end];
        if (end < text.Length) return $"{chunk}\n[truncated: characters {offset:N0}–{end:N0} of {text.Length:N0}; call again with offset={end} for more]";
        return offset > 0 ? $"{chunk}\n[end: characters {offset:N0}–{end:N0} of {text.Length:N0}]" : chunk;
    }

    private static string PageLines(IReadOnlyList<string> lines, int offset, int limit, string header)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 5_000);
        var page = lines.Skip(offset).Take(limit).ToArray();
        var text = new StringBuilder(header).AppendLine();
        foreach (var line in page) text.AppendLine(line);
        if (offset + page.Length < lines.Count)
            text.AppendLine($"[showing {offset + 1}–{offset + page.Length} of {lines.Count:N0}; call again with offset={offset + page.Length} for more]");
        return text.ToString().TrimEnd();
    }

    // Large textures are scaled down: a 4K texture as is would cost the model far more than it shows
    private static (byte[] Png, int Width, int Height, SKSizeI? Scaled) Scaled(byte[] png, int maxSize)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null) return (png, 0, 0, null);
        var (width, height) = (bitmap.Width, bitmap.Height);
        if (Math.Max(width, height) <= maxSize) return (png, width, height, null);
        var scale = (double) maxSize / Math.Max(width, height);
        var size = new SKSizeI(Math.Max(1, (int) Math.Round(width * scale)), Math.Max(1, (int) Math.Round(height * scale)));
        using var resized = bitmap.Resize(new SKImageInfo(size.Width, size.Height, bitmap.ColorType, bitmap.AlphaType), SKFilterQuality.High);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), width, height, size);
    }
}
