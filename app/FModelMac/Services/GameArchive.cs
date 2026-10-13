using System.Diagnostics;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse.UE4.VirtualFileSystem;
using Newtonsoft.Json;
using Serilog;

namespace FModelMac.Services;

public sealed record LocalizationResult(string Culture, int Count, string[] Available, bool Loaded);

public sealed record LoadOptions(
    string ArchiveDir,
    EGame Game,
    string? AesKey,
    IReadOnlyDictionary<string, string> DynamicKeys,
    string? UsmapPath,
    VersioningSettings? Versioning = null);

public sealed class FolderInfo
{
    public SortedSet<string> SubFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GameFile> Files { get; } = [];
}

public sealed record FolderIndex(IReadOnlyDictionary<string, FolderInfo> Folders, int FileCount);

// One container (.pak / .utoc) as listed in FModel's "Archives" tab
public sealed record ArchiveEntry(IAesVfsReader Reader, bool IsMounted)
{
    public string Name => Reader.Name;
    public long Length => Reader.Length;
    public string MountPoint => IsMounted ? Reader.MountPoint : "/";
    public bool IsEncrypted => Reader.IsEncrypted;
    public FGuid Guid => Reader.EncryptionKeyGuid;

    // Locked IoStore containers still know how many entries they hold (same trick as FModel)
    public int FileCount => IsMounted ? Reader.FileCount
        : Reader is IoStoreReader io ? (int) io.TocResource.Header.TocEntryCount - 1 : 0;
}

// A mounted game: provider, its containers, and folder indexes built on demand for the tree
public sealed class GameArchive : IDisposable
{
    private static readonly Lock OodleLock = new();

    public DefaultFileProvider Provider { get; }
    public IReadOnlyList<ArchiveEntry> Archives { get; }
    public string ProjectName => Provider.ProjectName;
    public TimeSpan LoadTime { get; }
    public int MountedCount => Archives.Count(a => a.IsMounted);
    public int LockedCount => Archives.Count(a => !a.IsMounted);

    private GameArchive(DefaultFileProvider provider, IReadOnlyList<ArchiveEntry> archives, TimeSpan loadTime)
    {
        Provider = provider;
        Archives = archives;
        LoadTime = loadTime;
    }

    public static GameArchive Load(LoadOptions options, IProgress<string> progress)
    {
        EnsureOodle();

        var sw = Stopwatch.StartNew();
        progress.Report("Loading game archives");
        var provider = new DefaultFileProvider(options.ArchiveDir, SearchOption.AllDirectories,
            (options.Versioning ?? new VersioningSettings()).CreateContainer(options.Game), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!string.IsNullOrWhiteSpace(options.UsmapPath))
                provider.MappingsContainer = Endpoints.OpenMappings(options.UsmapPath);

            provider.Initialize();
            provider.Mount();

            progress.Report("Decrypting archives");
            var keys = new Dictionary<FGuid, FAesKey>();
            if (!string.IsNullOrWhiteSpace(options.AesKey))
                keys[new FGuid()] = new FAesKey(options.AesKey.Trim());
            foreach (var (guid, key) in options.DynamicKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    keys[new FGuid(guid)] = new FAesKey(key.Trim());
            }
            if (keys.Count > 0) provider.SubmitKeys(keys);
            provider.PostMount();
            sw.Stop();

            // global.utoc holds no files: it is only read as GlobalData, so it is not listed
            static bool Listed(IAesVfsReader r) => !r.Name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase);
            var archives = provider.MountedVfs.Where(Listed).Select(r => new ArchiveEntry(r, true))
                .Concat(provider.UnloadedVfs.Where(Listed).Select(r => new ArchiveEntry(r, false)))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new GameArchive(provider, archives, sw.Elapsed);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>Folder index of the given archives (all mounted ones when <paramref name="scope"/> is null).</summary>
    public FolderIndex BuildIndex(IReadOnlySet<IVfsReader>? scope, Func<GameFile, bool>? include = null)
    {
        var folders = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase) { [""] = new() };
        // Values yields one entry per container, highest priority first (patch paks before base),
        // so keep only the first occurrence of each path: the same one Files[path] resolves to
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FolderInfo GetOrAdd(string path)
        {
            if (!folders.TryGetValue(path, out var info))
                folders[path] = info = new FolderInfo();
            return info;
        }

        foreach (var file in Provider.Files.Values)
        {
            // .uexp/.ubulk/.uptnl are payloads of a package, FModel hides them too
            if (file.IsUePackagePayload) continue;
            if (scope is not null && (file is not VfsEntry entry || !scope.Contains(entry.Vfs))) continue;
            if (!seen.Add(file.Path)) continue;
            if (include is not null && !include(file)) continue;

            var dir = file.Path.Contains('/') ? file.Directory : "";
            GetOrAdd(dir).Files.Add(file);

            // Register the folder in every ancestor; stop once an ancestor already knows it
            while (dir.Length > 0)
            {
                var slash = dir.LastIndexOf('/');
                var parent = slash < 0 ? "" : dir[..slash];
                if (!GetOrAdd(parent).SubFolders.Add(dir[(slash + 1)..])) break;
                dir = parent;
            }
        }

        foreach (var info in folders.Values)
            info.Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return new FolderIndex(folders, folders.Values.Sum(f => f.Files.Count));
    }

    private static void EnsureOodle()
    {
        lock (OodleLock)
        {
            if (OodleHelper.Instance is not null) return;

            // OodleHelper only knows Windows/Linux download names, but an existing path is loaded as-is
            var path = Path.Combine(AppContext.BaseDirectory, "liboodle-data-shared.dylib");
            if (File.Exists(path))
                OodleHelper.Initialize(path);
            else
                Log.Warning("Native Oodle not found at {Path}, using the managed decompressor", path);
        }
    }

    /// <summary>
    /// Loads the localized texts (FText) for a language, like FModel's LoadLocalizedResources. CUE4Parse maps the
    /// language to a culture code per game; when the game has no folder for that exact code (es vs es-ES),
    /// the closest culture it does ship is used.
    /// </summary>
    /// <summary>
    /// FModel's packages language. CUE4Parse only switches to cultures the game declares (CulturesToStage in its ini, or
    /// the .locmeta), so a game that doesn't declare them gets its .locres loaded here instead. Engine strings are left
    /// out, like CUE4Parse does.
    /// </summary>
    public LocalizationResult LoadLocalization(ELanguage language, bool includeEngine = false)
    {
        var locres = Provider.Files.Values
            .Where(f => f.Extension.Equals("locres", StringComparison.OrdinalIgnoreCase) &&
                        (includeEngine || !f.Path.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase)))
            .Select(f => (File: f, Culture: f.Path.Split('/') is { Length: >= 3 } parts ? parts[^2] : null))
            .Where(f => f.Culture is not null).ToArray();
        var available = Provider.Internationalization.AvailableCultures.Concat(locres.Select(f => f.Culture!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var wanted = Provider.GetLanguageCode(language);
        var culture = available.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                      ?? available.FirstOrDefault(c => c.Split('-')[0].Equals(wanted.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                      ?? wanted;

        if (!includeEngine && Provider.TryChangeCulture(culture))
            return new LocalizationResult(culture, Provider.Internationalization.Count, available, true);

        var files = locres.Where(f => f.Culture!.Equals(culture, StringComparison.OrdinalIgnoreCase)).Select(f => f.File).ToArray();
        var entries = new Dictionary<string, IDictionary<string, string>>();
        foreach (var file in files)
        {
            try
            {
                if (!file.TryCreateReader(out var archive)) continue;
                using (archive)
                {
                    foreach (var (ns, values) in new FTextLocalizationResource(archive).Entries)
                    {
                        if (!entries.TryGetValue(ns.Str, out var target)) entries[ns.Str] = target = new Dictionary<string, string>();
                        foreach (var (key, value) in values) target[key.Str] = value.LocalizedString;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not read {Path}", file.Path);
            }
        }
        Provider.Internationalization.Clear();
        Provider.Internationalization.Override(entries);
        return new LocalizationResult(culture, Provider.Internationalization.Count, available, files.Length > 0);
    }

    /// <summary>FModel's Show Metadata: the package summary, name map, imports and exports as JSON.</summary>
    public string PackageMetadata(GameFile file) => JsonConvert.SerializeObject(Provider.LoadPackage(file), Formatting.Indented);

    /// <summary>
    /// FModel's Decompile Blueprint: every class in the package as C++-like pseudocode, with FModel's clean-ups of
    /// the compiler's generated names. Null when the package has no class.
    /// </summary>
    public string? DecompileBlueprint(GameFile file)
    {
        // Function bodies are only read with ReadScriptData, which FModel leaves off by default: turn it on while
        // the package and its functions deserialize (exports load lazily, so for the whole decompilation)
        lock (_readOptionsLock)
        {
            Provider.ReadScriptData = true;
            try
            {
                return Decompile(file);
            }
            finally
            {
                Provider.ReadScriptData = _readScriptData;
            }
        }
    }

    private readonly Lock _readOptionsLock = new();
    private bool _readScriptData;

    /// <summary>FModel's "Serialize Script Bytecode" and "Serialize Inlined Shader Maps" (Settings › General).</summary>
    public void ApplyReadOptions(bool readScriptData, bool readShaderMaps)
    {
        lock (_readOptionsLock)
        {
            _readScriptData = readScriptData;
            Provider.ReadScriptData = readScriptData;
            Provider.ReadShaderMaps = readShaderMaps;
        }
    }

    private string? Decompile(GameFile file)
    {
        var package = Provider.LoadPackage(file);
        UClassCookedMetaData? cookedMetaData = null;
        try
        {
            cookedMetaData = package.GetExportOrNull<UClassCookedMetaData>("CookedClassMetaData");
        }
        catch
        {
            // ignored, like FModel: the metadata only improves the output
        }

        var classes = new List<string>();
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Class is null) continue;
            if (((AbstractUePackage) package).ConstructObject(pointer.Class, package) is not UClass) continue;
            if (pointer.Object?.Value is not UClass blueprint) continue;
            try
            {
                classes.Add(blueprint.DecompileBlueprintToPseudo(cookedMetaData));
            }
            catch (Exception e)
            {
                // One class CUE4Parse can't decompile shouldn't hide the others
                Log.Warning(e, "Could not decompile {Class}", blueprint.Name);
                classes.Add($"// {blueprint.Name}: {e.GetType().Name}: {e.Message}");
            }
        }
        if (classes.Count == 0) return null;

        var cpp = string.Join("\n\n", classes);
        if (file.Path.Contains("_Verse.uasset")) cpp = Regex.Replace(cpp, "__verse_0x[a-fA-F0-9]{8}_", ""); // UnmangleCasedName
        cpp = Regex.Replace(cpp, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");
        cpp = Regex.Replace(cpp, @"K2Node_DynamicCast_([A-Za-z0-9_]+)", "$1");
        return Regex.Replace(cpp, @"K2Node_([A-Za-z0-9_]+)", "$1");
    }

    private readonly Lock _importersLock = new();
    private Dictionary<string, GameFile[]>? _importers;

    /// <summary>Set once the pak import index has been built.</summary>
    public string? ReferenceIndexSummary { get; private set; }

    /// <summary>Whether Find References still has to read every package header of a pak game once.</summary>
    public bool NeedsReferenceIndex => _importers is null && Provider.Files.Values.Any(IsPakPackage);

    /// <summary>
    /// FModel's Find References: the packages that import this one. FModel only reads IoStore container headers;
    /// packages in .pak archives (UE4 before IoStore, or mixed games) are covered by an index of every package's
    /// imports, built once per mount from the package headers alone.
    /// </summary>
    public IReadOnlyList<GameFile> FindReferences(GameFile file, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!file.IsUePackage) return [];
        var refs = new HashSet<GameFile>();
        if (file is FIoStoreEntry) refs.UnionWith(Provider.ScanForPackageRefs(file));
        if (ImportIndex(progress, ct).TryGetValue(file.PathWithoutExtension, out var importers)) refs.UnionWith(importers);
        // The same package can sit in several containers: list the copy the provider uses, once
        return refs.Select(f => Provider.Files.TryGetValue(f.Path, out var used) ? used : f)
            .Where(f => !f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsPakPackage(GameFile file) => file is not FIoStoreEntry && file.IsUePackage;

    private Dictionary<string, GameFile[]> ImportIndex(IProgress<double>? progress, CancellationToken ct)
    {
        lock (_importersLock)
        {
            if (_importers is not null) return _importers;

            var sw = Stopwatch.StartNew();
            var packages = Provider.Files.Values.Where(IsPakPackage).ToArray();
            var imports = new (GameFile File, string[] Packages)[packages.Length];
            int done = 0, failed = 0;
            Parallel.For(0, packages.Length, new ParallelOptions { CancellationToken = ct }, i =>
            {
                try
                {
                    if (packages[i].TryCreateReader(out var archive))
                    {
                        using (archive)
                        {
                            // Header only: the summary, names, imports and exports live in the .uasset
                            var package = new Package(archive, null, ubulk: (FArchive?) null, provider: Provider);
                            imports[i] = (packages[i], package.ImportMap
                                .Where(import => import.ClassName.Text == "Package")
                                .Select(import => import.ObjectName.Text).ToArray());
                        }
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
                var count = Interlocked.Increment(ref done);
                if (count % 500 == 0) progress?.Report((double) count / packages.Length);
            });

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = new Dictionary<string, List<GameFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (importer, names) in imports)
            {
                if (importer is null) continue;
                foreach (var name in names)
                {
                    if (name.Length < 2 || name[0] != '/') continue; // script packages (/Script/Engine) have no file
                    if (!paths.TryGetValue(name, out var path)) paths[name] = path = Provider.FixPath(name).SubstringBeforeLast('.');
                    if (!index.TryGetValue(path, out var list)) index[path] = list = [];
                    list.Add(importer);
                }
            }
            ReferenceIndexSummary = $"Indexed the imports of {packages.Length:N0} packages in {sw.Elapsed.TotalSeconds:F2}s" +
                                    (failed > 0 ? $" ({failed:N0} unreadable)" : "");
            Log.Information(ReferenceIndexSummary);
            return _importers = index.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Dispose() => Provider.Dispose();
}
