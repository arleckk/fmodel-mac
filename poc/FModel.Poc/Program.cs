using System.Diagnostics;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Serilog;

namespace FModel.Poc;

// Proof of concept: run the CUE4Parse core (the parsing engine behind FModel) natively on macOS.
// Mounts a game's archives, lists files, exports assets as JSON and textures as PNG.
public static class Program
{
    private const string Usage = """
        Usage: FModel.Poc <archive-dir> [options]

          --game <EGame>        Engine version, e.g. GAME_UE5_8 (default GAME_UE5_8)
          --aes <0xHEX>         AES key for encrypted containers
          --aes-ascii <text>    AES key given as 32 ASCII chars (test fixtures)
          --usmap <file>        Mappings file, required for unversioned games
          --global <file>       Extra global.utoc to register (when it lives elsewhere)
          --oodle <native|managed|path>  Oodle backend (default: native dylib next to the exe)
          --list <N>            Print the first N file paths (default 15)
          --export <suffix>     Export package(s) whose path ends with suffix (repeatable)
          --out <dir>           Output directory (default ./Output)
          --read-all            Read+decompress every file and report throughput
        """;

    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateLogger();

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var opts = Options.Parse(args);
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " +
                          $"on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                          $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"CUE4Parse-Natives loaded: {CUE4ParseNatives.IsInitialized}");

        InitOodle(opts.Oodle);

        var total = Stopwatch.StartNew();
        var provider = new DefaultFileProvider(opts.ArchiveDir, SearchOption.AllDirectories,
            new VersionContainer(opts.Game), StringComparer.OrdinalIgnoreCase);
        if (opts.Usmap is not null)
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(opts.Usmap);

        provider.Initialize();
        if (opts.Global is not null) provider.RegisterVfs(opts.Global);

        // Same order as FModel: mount unencrypted containers, then unlock the encrypted ones
        var mounted = provider.Mount();
        if (opts.AesKey is not null)
            mounted += provider.SubmitKey(new FGuid(), opts.AesKey);
        provider.PostMount();
        total.Stop();

        // global.utoc holds no files: it is only read as GlobalData, so it always stays in UnloadedVfs
        var locked = provider.UnloadedVfs
            .Where(v => !v.Name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Console.WriteLine($"Containers: {provider.MountedVfs.Count} mounted, {locked.Length} still locked " +
                          $"(this call mounted {mounted}), IoStore global data: {(provider.GlobalData is not null ? "loaded" : "none")}");
        foreach (var vfs in locked)
            Console.WriteLine($"  locked: {vfs.Name} (encrypted: {vfs.IsEncrypted}, key guid: {vfs.EncryptionKeyGuid})");
        Console.WriteLine($"Files: {provider.Files.Count:N0} in {total.ElapsedMilliseconds} ms");

        foreach (var path in provider.Files.Keys.Order(StringComparer.OrdinalIgnoreCase).Take(opts.List))
            Console.WriteLine($"  {path}");

        if (opts.ReadAll) ReadAll(provider);

        var failures = 0;
        foreach (var suffix in opts.Exports)
            failures += Export(provider, suffix, opts.OutDir) ? 0 : 1;

        return failures == 0 ? 0 : 2;
    }

    private static void InitOodle(string mode)
    {
        if (mode == "managed")
        {
            Console.WriteLine("Oodle: managed fallback (OodleSharp)");
            return;
        }

        var path = mode == "native"
            ? Path.Combine(AppContext.BaseDirectory, "liboodle-data-shared.dylib")
            : Path.GetFullPath(mode);
        if (!File.Exists(path))
        {
            Console.WriteLine($"Oodle: native library not found at {path}, using managed fallback");
            return;
        }

        // OodleHelper only knows Windows/Linux download names, but an existing path is loaded as-is.
        OodleHelper.Initialize(path);
        Console.WriteLine($"Oodle: native {(OodleHelper.Instance is not null ? "loaded" : "FAILED")} ({path})");
    }

    private static void ReadAll(DefaultFileProvider provider)
    {
        long bytes = 0;
        var errors = 0;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var sw = Stopwatch.StartNew();
        foreach (var file in provider.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = file.Read();
                hash.AppendData(data);
                bytes += data.Length;
            }
            catch (Exception e)
            {
                if (errors++ < 3) Log.Warning(e, "Failed to read {Path}", file.Path);
            }
        }
        sw.Stop();
        Console.WriteLine($"Read-all: {provider.Files.Count:N0} files, {bytes / 1024.0 / 1024.0:F1} MiB " +
                          $"in {sw.ElapsedMilliseconds} ms ({bytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds:F0} MiB/s), " +
                          $"{errors} errors, sha256 {Convert.ToHexString(hash.GetHashAndReset())[..16]}");
    }

    private static bool Export(DefaultFileProvider provider, string suffix, string outDir)
    {
        var matches = provider.Files.Values
            .Where(f => f.IsUePackage && f.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            Console.WriteLine($"Export '{suffix}': no matching package");
            return false;
        }

        var ok = true;
        foreach (var file in matches)
        {
            try
            {
                var exports = provider.LoadPackage(file).GetExports().ToArray();
                var baseName = Path.Combine(outDir, file.PathWithoutExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(baseName)!);

                File.WriteAllText(baseName + ".json", JsonConvert.SerializeObject(exports, Formatting.Indented));
                Console.WriteLine($"Exported {file.Path}: {exports.Length} export(s) " +
                                  $"[{string.Join(", ", exports.Select(e => e.ExportType).Distinct())}] -> {baseName}.json");

                foreach (var texture in exports.OfType<UTexture>())
                {
                    var decoded = texture.Decode();
                    if (decoded is null)
                    {
                        Console.WriteLine($"  texture {texture.Name}: decode returned null");
                        ok = false;
                        continue;
                    }

                    var png = decoded.Encode(ETextureFormat.Png, false, out var ext);
                    var pngPath = $"{baseName}_{texture.Name}.{ext}";
                    File.WriteAllBytes(pngPath, png);
                    Console.WriteLine($"  texture {texture.Name}: {decoded.Width}x{decoded.Height} " +
                                      $"{texture.Format} -> {pngPath}");
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to export {Path}", file.Path);
                ok = false;
            }
        }
        return ok;
    }

    private sealed class Options
    {
        public string ArchiveDir = "";
        public EGame Game = EGame.GAME_UE5_8;
        public FAesKey? AesKey;
        public string? Usmap;
        public string? Global;
        public string Oodle = "native";
        public int List = 15;
        public readonly List<string> Exports = [];
        public string OutDir = Path.GetFullPath("Output");
        public bool ReadAll;

        public static Options Parse(string[] args)
        {
            var o = new Options { ArchiveDir = Path.GetFullPath(args[0]) };
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--game": o.Game = Enum.Parse<EGame>(Next(), true); break;
                    case "--aes": o.AesKey = new FAesKey(Next()); break;
                    case "--aes-ascii": o.AesKey = new FAesKey(Encoding.ASCII.GetBytes(Next())); break;
                    case "--usmap": o.Usmap = Path.GetFullPath(Next()); break;
                    case "--global": o.Global = Path.GetFullPath(Next()); break;
                    case "--oodle": o.Oodle = Next(); break;
                    case "--list": o.List = int.Parse(Next()); break;
                    case "--export": o.Exports.Add(Next()); break;
                    case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                    case "--read-all": o.ReadAll = true; break;
                    default: throw new ArgumentException($"Unknown option {args[i]}\n{Usage}");
                }
            }
            return o;
        }
    }
}
