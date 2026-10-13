using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;
using Serilog.Events;

namespace FModel.Headless;

/// <summary>
/// The command line: <c>FModel &lt;command&gt;</c> (or the <c>fmodel</c> link to it) reads a game without opening the
/// window, with the app's profiles and settings. Results go to stdout, messages to stderr.
/// </summary>
public static class Cli
{
    private const string Usage = """
        Usage: fmodel <command> [options]

        Commands:
          profiles                     List the saved game profiles (* is the active one)
          info                         Mount the game and summarize it: archives, files, locked archives
          ls [folder]                  List a folder (the root by default); -r lists every file below it
          search <text>                Find files: text in the path, a wildcard (*, ?) or --regex
          cat <path>                   Print a file as the app shows it: properties JSON for packages
                                       (--export <name> for one export), text, decompiled Lua, or hex
          metadata <path>              Print a package's summary, names, imports and exports as JSON
          blueprint <path>             Print the decompiled Blueprint classes of a package
          refs <path>                  List the packages that reference a package
          export <path|folder>...      Export with the app's settings into its output folder
                                       --kind properties|raw|textures|models|animations|worlds|audio|code
          mcp                          Run the MCP server over stdio, for AI assistants
          version                      Print the version

        Game (every command but profiles and version):
          -p, --profile <name>         A saved profile (default: the active one in the app)
          -d, --dir <folder>           A game's Paks folder instead of a profile
          -g, --game <version>         UE version, as in the app (GAME_UE5_4, GAME_UE4_27…)
          -k, --aes <key>              Main AES key
          -m, --mappings <file>        Mappings (.usmap, .jmap)
              --refresh-aes            Ask the profile's AES endpoint for keys first

        Options:
          -o, --output <path>          export: the output folder; cat, metadata, blueprint: a file to write
              --json                   profiles, info, ls, search, refs, export: JSON output
              --limit <n>              search: at most n results (default 200, 0 for all)
              --folder <folder>        search: only below this folder
              --regex                  search: <text> is a regular expression
          -r, --recursive              ls: every file below the folder
          -v, --verbose                Show the loading log on stderr

        Paths are game paths (Game/Content/Maps/Map.umap) or package paths (/Game/Maps/Map).
        Settings, profiles and logs are the app's (~/Library/Application Support/FModel); the command line never
        changes them.
        """;

    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "profiles", "info", "ls", "search", "cat", "metadata", "blueprint", "refs", "export", "mcp", "version", "help",
        "--help", "-h", "--version"
    };

    /// <summary>
    /// Whether the arguments are for the command line: a launch from Finder or the Dock has none (older macOS passed a
    /// -psn_ process serial number, and Xcode passes -NS… defaults), so anything else is a command, known or not.
    /// </summary>
    public static bool Handles(string[] args) =>
        args.Length > 0 && !args[0].StartsWith("-psn_", StringComparison.Ordinal) && !args[0].StartsWith("-NS", StringComparison.Ordinal);

    private static readonly JsonSerializerSettings JsonOutput = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented
    };

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            if (!Commands.Contains(args[0])) throw new HeadlessException($"Unknown command '{args[0]}'");
            options = Options.Parse(args);
        }
        catch (HeadlessException e)
        {
            Console.Error.WriteLine($"fmodel: {e.Message}");
            Console.Error.WriteLine("Run 'fmodel help' for the commands and options.");
            return 2;
        }

        // stdout carries results (and the MCP protocol): logs go to a file of their own, and to stderr with -v
        var logger = new LoggerConfiguration()
            .WithExportLogs()
            .WriteTo.File(Path.Combine(Program.LogDirectory, "FModel-cli-.log"), restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7);
        if (options.Verbose)
            logger = logger.WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning, standardErrorFromLevel: LogEventLevel.Verbose);
        Log.Logger = logger.CreateLogger();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            return RunAsync(options, cts.Token).GetAwaiter().GetResult();
        }
        catch (HeadlessException e)
        {
            Console.Error.WriteLine($"fmodel: {e.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("fmodel: canceled");
            return 130;
        }
        catch (Exception e)
        {
            Log.Error(e, "Command {Command} failed", options.Command);
            Console.Error.WriteLine($"fmodel: {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task<int> RunAsync(Options o, CancellationToken ct)
    {
        switch (o.Command)
        {
            case "help" or "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            case "version" or "--version":
                Console.WriteLine($"FModel {typeof(Cli).Assembly.GetName().Version?.ToString(3)} (macOS)");
                return 0;
            case "profiles":
                return Profiles(o);
            case "mcp":
                return await FModelMcpServer.RunAsync(o.Target, ct);
        }

        Action<string>? log = o.Verbose ? m => Console.Error.WriteLine(m) : null;
        using var game = await HeadlessGame.LoadAsync(o.Target, log, ct);
        switch (o.Command)
        {
            case "info":
                Info(game, o);
                return 0;
            case "ls":
                return List(game, o);
            case "search":
                return Search(game, o);
            case "cat":
                return Write(game.Show(game.Resolve(o.Single("a path")), o.Export, ct), o);
            case "metadata":
                return Write(game.Archive.PackageMetadata(Package(game, o.Single("a path"))), o);
            case "blueprint":
                var file = Package(game, o.Single("a path"));
                return Write(game.Archive.DecompileBlueprint(file) ?? throw new HeadlessException($"{file.Name} has no Blueprint class"), o);
            case "refs":
                return References(game, o, ct);
            default:
                return await Export(game, o, log, ct);
        }
    }

    private static int Profiles(Options o)
    {
        var profiles = HeadlessGame.Profiles();
        if (o.Json)
        {
            Console.WriteLine(JsonConvert.SerializeObject(profiles, JsonOutput));
            return 0;
        }
        if (profiles.Count == 0)
        {
            Console.Error.WriteLine("No saved profiles: create one in FModel (Directory › Profiles), or pass --dir");
            return 0;
        }
        var width = profiles.Max(p => p.Name.Length);
        var gameWidth = profiles.Max(p => p.Game.Length);
        foreach (var p in profiles)
            Console.WriteLine($"{(p.Active ? '*' : ' ')} {p.Name.PadRight(width)}  {p.Game.PadRight(gameWidth)}  {p.Directory}");
        return 0;
    }

    private static void Info(HeadlessGame game, Options o)
    {
        var s = game.Summary();
        if (o.Json)
        {
            Console.WriteLine(JsonConvert.SerializeObject(s, JsonOutput));
            return;
        }
        Console.WriteLine($"Game:      {s.Name} ({s.Project}, {s.Game})");
        Console.WriteLine($"Directory: {s.Directory}");
        Console.WriteLine($"Archives:  {s.Mounted}/{s.Archives} mounted, loaded in {s.LoadSeconds:F2}s");
        Console.WriteLine($"Files:     {s.Files:N0}");
        Console.WriteLine($"Mappings:  {s.Mappings ?? "none"}");
        Console.WriteLine($"Language:  {s.Language}");
        if (s.Locked.Count > 0) Console.WriteLine($"Locked:    {string.Join(", ", s.Locked)} (need an AES key)");
    }

    private static int List(HeadlessGame game, Options o)
    {
        var path = o.Positional.Count > 0 ? o.Single("a folder") : "";
        var folder = game.FindFolder(path) ?? throw new HeadlessException($"No folder '{path}' in {game.Name}");
        var info = game.Index.Folders[folder];
        var prefix = folder.Length == 0 ? "" : folder + "/";
        if (o.Recursive)
        {
            var files = game.FilesUnder(folder).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (o.Json) Console.WriteLine(JsonConvert.SerializeObject(files.Select(FileEntry), JsonOutput));
            else foreach (var f in files) Console.WriteLine(f.Path);
            return 0;
        }
        if (o.Json)
        {
            Console.WriteLine(JsonConvert.SerializeObject(new
            {
                Folder = folder,
                Folders = info.SubFolders.Select(sub => prefix + sub),
                Files = info.Files.Select(FileEntry)
            }, JsonOutput));
            return 0;
        }
        foreach (var sub in info.SubFolders) Console.WriteLine($"{prefix}{sub}/");
        foreach (var f in info.Files) Console.WriteLine($"{f.Path}  {AssetPreview.FormatSize(f.Size)}");
        return 0;
    }

    private static int Search(HeadlessGame game, Options o)
    {
        var query = o.Single("the text to search for");
        string? folder = null;
        if (o.Folder is not null) folder = game.FindFolder(o.Folder) ?? throw new HeadlessException($"No folder '{o.Folder}' in {game.Name}");
        var found = game.Search(query, folder, o.Regex, o.Limit, out var total);
        if (o.Json) Console.WriteLine(JsonConvert.SerializeObject(new { Total = total, Files = found.Select(FileEntry) }, JsonOutput));
        else foreach (var f in found) Console.WriteLine(f.Path);
        if (found.Count < total) Console.Error.WriteLine($"{found.Count} of {total:N0} matches (--limit 0 lists them all)");
        else if (total == 0) Console.Error.WriteLine("No matches");
        return 0;
    }

    private static int References(HeadlessGame game, Options o, CancellationToken ct)
    {
        var file = Package(game, o.Single("a path"));
        if (game.Archive.NeedsReferenceIndex) Console.Error.WriteLine("Indexing the imports of every package once (kept for next time)…");
        var refs = game.Archive.FindReferences(file, null, ct);
        if (o.Json) Console.WriteLine(JsonConvert.SerializeObject(refs.Select(f => f.Path), JsonOutput));
        else foreach (var f in refs) Console.WriteLine(f.Path);
        if (refs.Count == 0) Console.Error.WriteLine($"Nothing references {file.Name}");
        return 0;
    }

    private static async Task<int> Export(HeadlessGame game, Options o, Action<string>? log, CancellationToken ct)
    {
        if (o.Kind is null) throw new HeadlessException($"export needs --kind ({HeadlessExport.KindList})");
        var kind = HeadlessExport.ParseKind(o.Kind);
        if (o.Positional.Count == 0) throw new HeadlessException("export needs a path or a folder");
        if (o.Output is not null) game.Settings.OutputDir = Path.GetFullPath(o.Output);

        var files = new List<GameFile>();
        foreach (var path in o.Positional)
        {
            if (game.FindFolder(path) is { } folder) files.AddRange(game.FilesUnder(folder));
            else files.Add(game.Resolve(path));
        }
        var report = await HeadlessExport.RunAsync(game, files.DistinctBy(f => f.Path).ToArray(), kind, log, ct);
        if (o.Json) Console.WriteLine(JsonConvert.SerializeObject(report, JsonOutput));
        else foreach (var path in report.Files) Console.WriteLine(path);
        foreach (var error in report.Errors.Take(20)) Console.Error.WriteLine($"error: {error}");
        if (report.Errors.Count > 20) Console.Error.WriteLine($"… {report.Errors.Count - 20} more errors in {Program.LogDirectory}");
        Console.Error.WriteLine($"Exported {report.Files.Count} file(s) to {report.Directory}" +
                                (report.Empty > 0 ? $"; {report.Empty} had no {o.Kind}" : "") +
                                (report.Errors.Count > 0 ? $"; {report.Errors.Count} error(s)" : ""));
        return report.Files.Count == 0 && report.Errors.Count > 0 ? 1 : 0;
    }

    private static GameFile Package(HeadlessGame game, string path)
    {
        var file = game.Resolve(path);
        return file.IsUePackage ? file : throw new HeadlessException($"'{file.Path}' isn't a package (.uasset/.umap)");
    }

    private static object FileEntry(GameFile f) => new { f.Path, f.Size };

    private static int Write(string text, Options o)
    {
        if (o.Output is null)
        {
            Console.Out.Write(text);
            if (!text.EndsWith('\n')) Console.Out.WriteLine();
            return 0;
        }
        var path = Path.GetFullPath(o.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Console.Error.WriteLine($"Saved {path}");
        return 0;
    }

    private sealed class Options
    {
        public string Command = "";
        public readonly List<string> Positional = [];
        public string? Profile, Directory, Game, Aes, Mappings, Output, Kind, Export, Folder;
        public bool Json, Recursive, Regex, Verbose, RefreshAes;
        public int Limit = 200;

        public GameTarget Target => new(Profile, Directory, Game, Aes, Mappings, RefreshAes);

        public string Single(string what) => Positional.Count switch
        {
            1 => Positional[0],
            0 => throw new HeadlessException($"{Command} needs {what}"),
            _ => throw new HeadlessException($"{Command} takes one argument, got {Positional.Count}: quote paths with spaces")
        };

        public static Options Parse(string[] args)
        {
            var o = new Options { Command = args[0].ToLowerInvariant() };
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                string Value() => i + 1 < args.Length ? args[++i] : throw new HeadlessException($"{arg} needs a value");
                switch (arg)
                {
                    case "-p" or "--profile": o.Profile = Value(); break;
                    case "-d" or "--dir": o.Directory = Value(); break;
                    case "-g" or "--game": o.Game = Value(); break;
                    case "-k" or "--aes": o.Aes = Value(); break;
                    case "-m" or "--mappings": o.Mappings = Value(); break;
                    case "-o" or "--output": o.Output = Value(); break;
                    case "--kind": o.Kind = Value(); break;
                    case "--export": o.Export = Value(); break;
                    case "--folder": o.Folder = Value(); break;
                    case "--limit":
                        o.Limit = int.TryParse(Value(), out var limit) && limit >= 0 ? limit : throw new HeadlessException("--limit needs a number");
                        break;
                    case "--json": o.Json = true; break;
                    case "-r" or "--recursive": o.Recursive = true; break;
                    case "--regex": o.Regex = true; break;
                    case "-v" or "--verbose": o.Verbose = true; break;
                    case "--refresh-aes": o.RefreshAes = true; break;
                    case "--": o.Positional.AddRange(args[(i + 1)..]); i = args.Length; break;
                    default:
                        if (arg.Length > 1 && arg[0] == '-') throw new HeadlessException($"Unknown option '{arg}'");
                        o.Positional.Add(arg);
                        break;
                }
            }
            return o;
        }
    }
}
