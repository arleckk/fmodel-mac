using System.Text.RegularExpressions;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using FModel.Services;
using FModel.Services.Audio;
using Newtonsoft.Json;

namespace FModel.Headless;

/// <summary>An error worth showing as is: a wrong path, an unknown profile, a game that can't be mounted.</summary>
public sealed class HeadlessException(string message) : Exception(message);

/// <summary>
/// The game the command line and the MCP server work on: a saved profile (the app's Directory › Profiles; the active
/// one by default), or a game directory given directly. Options given along with a profile override its own.
/// </summary>
/// <param name="RefreshAes">Ask the profile's AES endpoint for keys first (the app does it per "AES Reload at Launch").</param>
public sealed record GameTarget(string? Profile = null, string? Directory = null, string? Game = null, string? AesKey = null,
    string? Mappings = null, bool RefreshAes = false);

/// <summary>A saved profile as the command line lists it (keys are never shown).</summary>
public sealed record ProfileInfo(string Name, string Game, string? Directory, bool Active, bool HasAesKey, int DynamicKeys, string? Mappings);

/// <summary>What <c>info</c> and the MCP server's <c>game_info</c> tell about a mounted game.</summary>
public sealed record GameSummary(string Name, string Directory, string Game, string Project, int Files, int Archives, int Mounted,
    IReadOnlyList<string> Locked, double LoadSeconds, string? Mappings, string Language);

/// <summary>
/// A game mounted without the app's window, for the command line and the MCP server: the app's settings and services
/// (archives, endpoints, exports), read from the same data folder and never saved back, so the app stays the one
/// that changes them.
/// </summary>
public sealed class HeadlessGame : IDisposable
{
    public GameArchive Archive { get; }
    /// <summary>The app's settings with the target applied (in memory only).</summary>
    public AppSettings Settings { get; }
    /// <summary>The profile's name, or the game directory's suggested one.</summary>
    public string Name { get; }
    public string? MappingsFile { get; }

    private HeadlessGame(GameArchive archive, AppSettings settings, string name, string? mappings)
    {
        Archive = archive;
        Settings = settings;
        Name = name;
        MappingsFile = mappings;
        _index = new Lazy<FolderIndex>(() => archive.BuildIndex(null));
    }

    public static IReadOnlyList<ProfileInfo> Profiles()
    {
        var settings = AppSettings.Load();
        return settings.Profiles.Select(p => new ProfileInfo(p.Name, p.Game, p.ArchiveDir, p.Name == settings.CurrentProfile,
            !string.IsNullOrWhiteSpace(p.AesKey), p.DynamicKeys.Count,
            p.UsmapPath is { Length: > 0 } usmap ? usmap : p.MappingEndpoint is { IsUsable: true } ? "mapping endpoint" : null)).ToArray();
    }

    /// <summary>Mounts the game like the app does at launch: keys, mappings (file or endpoint), archives, versioning.</summary>
    /// <param name="log">What the app would write to its log panel.</param>
    public static async Task<HeadlessGame> LoadAsync(GameTarget target, Action<string>? log = null, CancellationToken ct = default)
    {
        var settings = AppSettings.Load();
        var name = Configure(target, settings);
        if (!Directory.Exists(settings.ArchiveDir))
            throw new HeadlessException($"The game directory '{settings.ArchiveDir}' of {name} doesn't exist");
        if (!Enum.TryParse<EGame>(settings.Game, true, out var game))
            throw new HeadlessException($"Unknown UE version '{settings.Game}' (for example GAME_UE5_4 or GAME_UE4_27)");

        if (target.RefreshAes) await RefreshAesAsync(settings, log, ct);
        var mappings = await ResolveMappingsAsync(settings, log, ct);
        var options = new LoadOptions(settings.ArchiveDir!, game, settings.AesKey, settings.DynamicKeys, mappings, settings.CurrentVersioning);
        var archive = await Task.Run(() => GameArchive.Load(options, new InlineProgress<string>(_ => { })), ct);
        try
        {
            archive.ApplyReadOptions(settings.ReadScriptData, settings.ReadShaderMaps);
            log?.Invoke($"{archive.ProjectName} ({game}) | Mounted: {archive.MountedCount}/{archive.Archives.Count} | " +
                        $"Files: x{archive.Provider.Files.Count:N0} | {archive.LoadTime.TotalSeconds:F2}s");
            if (archive.LockedCount > 0)
                log?.Invoke($"{archive.LockedCount} archive(s) need an AES key: {string.Join(", ", archive.Archives.Where(a => !a.IsMounted).Select(a => a.Name).Take(5))}");
            return new HeadlessGame(archive, settings, name, mappings);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    // The profile (or directory) becomes the settings' current game, as Directory › Profiles does in the app
    private static string Configure(GameTarget target, AppSettings settings)
    {
        string name;
        if (!string.IsNullOrWhiteSpace(target.Directory))
        {
            var directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target.Directory.Trim()));
            settings.ArchiveDir = directory;
            settings.Game = "GAME_UE5_LATEST";
            settings.AesKey = null;
            settings.DynamicKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.UsmapPath = null;
            settings.AesEndpoint = settings.MappingEndpoint = null;
            settings.CurrentProfile = null;
            name = GameProfile.SuggestName(directory);
        }
        else
        {
            var wanted = target.Profile ?? settings.CurrentProfile;
            if (wanted is null)
                throw new HeadlessException(settings.Profiles.Count == 0
                    ? "No saved profiles: create one in FModel (Directory › Profiles), or pass a game directory"
                    : $"No active profile: pick one of {Names(settings)}");
            var profile = settings.FindProfile(wanted)
                          ?? throw new HeadlessException($"No profile '{wanted}'" + (settings.Profiles.Count > 0 ? $": pick one of {Names(settings)}" : ""));
            settings.ApplyProfile(profile);
            name = profile.Name;
        }

        if (!string.IsNullOrWhiteSpace(target.Game)) settings.Game = target.Game.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(target.AesKey))
        {
            settings.AesKey = Endpoints.FixKey(target.AesKey);
            if (settings.AesKey.Length != 66) throw new HeadlessException("The AES key must be 64 hexadecimal characters (with or without 0x)");
        }
        if (!string.IsNullOrWhiteSpace(target.Mappings))
        {
            settings.UsmapPath = Path.GetFullPath(target.Mappings.Trim());
            if (!File.Exists(settings.UsmapPath)) throw new HeadlessException($"The mappings file '{settings.UsmapPath}' doesn't exist");
        }
        return name;
    }

    private static string Names(AppSettings settings) => string.Join(", ", settings.Profiles.Select(p => $"'{p.Name}'"));

    // The app's RefreshAes, without saving the keys
    private static async Task RefreshAesAsync(AppSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (settings.AesEndpoint is not { IsUsable: true } endpoint)
        {
            log?.Invoke("No AES endpoint configured for this game");
            return;
        }
        try
        {
            var aes = await Endpoints.GetAesAsync(endpoint, ct);
            if (!aes.IsValid)
            {
                log?.Invoke($"The AES endpoint returned no usable key, keeping the saved ones ({endpoint.Url})");
                return;
            }
            if (aes.MainKey.Length == 66) settings.AesKey = aes.MainKey;
            settings.DynamicKeys = aes.DynamicKeys.GroupBy(k => k.Guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
            log?.Invoke($"AES keys from the endpoint: {(aes.MainKey.Length == 66 ? "main key" : "no main key")} + {aes.DynamicKeys.Count} dynamic key(s)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"Could not refresh AES keys from {endpoint.Url}: {e.Message}");
        }
    }

    // The app's InitMappings: a local mapping file first, then the mapping endpoint (cached)
    private static async Task<string?> ResolveMappingsAsync(AppSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.UsmapPath))
        {
            log?.Invoke($"Mappings pulled from '{Path.GetFileName(settings.UsmapPath)}'");
            return settings.UsmapPath;
        }
        if (settings.MappingEndpoint is not { IsUsable: true } endpoint) return null;
        var (path, source) = await Endpoints.ResolveMappingAsync(endpoint, ct: ct);
        log?.Invoke($"Mappings pulled from the endpoint: {source}");
        return path;
    }

    public GameSummary Summary() => new(Name, Settings.ArchiveDir!, Settings.Game, Archive.ProjectName, Index.FileCount,
        Archive.Archives.Count, Archive.MountedCount, Archive.Archives.Where(a => !a.IsMounted).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        Math.Round(Archive.LoadTime.TotalSeconds, 2), MappingsFile, Settings.AssetLanguageValue.ToString());

    // ---------- Localized texts ----------

    private readonly Lock _localizationLock = new();
    private bool _localized;

    /// <summary>The app loads the packages language at launch; here it waits for the first properties that need it.</summary>
    public void EnsureLocalization()
    {
        lock (_localizationLock)
        {
            if (_localized) return;
            _localized = true;
            try
            {
                Archive.LoadLocalization(Settings.AssetLanguageValue);
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not load localization for {Language}", Settings.AssetLanguageValue);
            }
        }
    }

    // ---------- Paths, folders and search ----------

    private readonly Lazy<FolderIndex> _index;

    /// <summary>Every listed file by folder, like the app's Folders tab (package payloads hidden).</summary>
    public FolderIndex Index => _index.Value;

    /// <summary>A file by its game path ("Game/Content/Maps/X.umap"), its package path ("/Game/Maps/X") or object path.</summary>
    public GameFile Resolve(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length > 0 && Archive.Provider.TryGetGameFile(trimmed, out var file)) return file;
        throw new HeadlessException($"No file '{path}' in {Name}: paths look like '{ExamplePath()}' (search finds files by name)");
    }

    /// <summary>The folder's path as the index spells it, or null when there's no such folder ("" is the root).</summary>
    public string? FindFolder(string? path)
    {
        var trimmed = (path ?? "").Trim().Replace('\\', '/').Trim('/');
        if (Index.Folders.ContainsKey(trimmed)) return Index.Folders.Keys.First(k => k.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (trimmed.Length == 0) return null;
        var fixedPath = Archive.Provider.FixPath(trimmed + "/").TrimEnd('/');
        return Index.Folders.Keys.FirstOrDefault(k => k.Equals(fixedPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The files of a folder and its subfolders.</summary>
    public IEnumerable<GameFile> FilesUnder(string folder)
    {
        if (!Index.Folders.TryGetValue(folder, out var info)) yield break;
        foreach (var file in info.Files) yield return file;
        foreach (var sub in info.SubFolders)
        foreach (var file in FilesUnder(folder.Length == 0 ? sub : $"{folder}/{sub}"))
            yield return file;
    }

    /// <summary>
    /// Files whose path contains <paramref name="query"/> (case-insensitive); with * or ? it's a wildcard pattern over the
    /// file name (or the whole path when it has a /), and with <paramref name="regex"/> a regular expression over the path.
    /// </summary>
    public IReadOnlyList<GameFile> Search(string query, string? folder, bool regex, int limit, out int total)
    {
        Func<GameFile, bool> match;
        if (regex)
        {
            Regex pattern;
            try
            {
                pattern = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw new HeadlessException($"Invalid regular expression: {e.Message}");
            }
            match = f => pattern.IsMatch(f.Path);
        }
        else if (query.IndexOfAny(['*', '?']) >= 0)
        {
            var pattern = new Regex("^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            match = query.Contains('/') ? f => pattern.IsMatch(f.Path) : f => pattern.IsMatch(f.Name);
        }
        else match = f => f.Path.Contains(query, StringComparison.OrdinalIgnoreCase);

        var scope = folder is null ? Index.Folders.Values.SelectMany(f => f.Files) : FilesUnder(folder);
        var found = scope.Where(match).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        total = found.Count;
        return limit > 0 && found.Count > limit ? found[..limit] : found;
    }

    private string ExamplePath() =>
        Index.Folders.Values.SelectMany(f => f.Files).FirstOrDefault(f => f.IsUePackage)?.Path ?? $"{Archive.ProjectName}/Content/…/Asset.uasset";

    // ---------- What the app shows for a file ----------

    /// <summary>The package's exports as FModel's JSON; only the named export when given.</summary>
    public string Properties(GameFile file, string? exportName = null, CancellationToken ct = default)
    {
        if (!file.IsUePackage) throw new HeadlessException($"'{file.Path}' isn't a package (.uasset/.umap)");
        EnsureLocalization();
        var exports = AssetPreview.LoadExports(Archive.Provider, file, ct);
        if (exportName is not null)
        {
            var named = exports.Where(e => e.Name.Equals(exportName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length == 0)
                throw new HeadlessException($"No export '{exportName}' in {file.Name}: it has {string.Join(", ", exports.Select(e => e.Name))}");
            exports = named;
        }
        return JsonConvert.SerializeObject(exports, Formatting.Indented);
    }

    /// <summary>
    /// The app's text tab for any file: properties JSON for packages, text files as they are, compiled Lua decompiled
    /// (Settings › Lua), a hex dump for the rest.
    /// </summary>
    public string Show(GameFile file, string? exportName = null, CancellationToken ct = default)
    {
        if (file.IsUePackage) return Properties(file, exportName, ct);
        var lua = Settings.DecompileLua ? LuaOptions.From(Settings) : null;
        var preview = AssetPreview.Build(Archive.Provider, file, ct, null, lua, Settings.CurrentVersioning.PlatformValue);
        if (preview.Text.Length == 0 && preview.Images is { Count: > 0 })
            return $"{file.Path}: {file.Extension.ToUpperInvariant()} image, {AssetPreview.FormatSize(file.Size)}";
        return preview.Warning is { } warning ? $"// {warning}\n{preview.Text}" : preview.Text;
    }

    /// <summary>The package's textures (each layer of an array, cubemaps unwrapped) as PNG, or an image file itself.</summary>
    public IReadOnlyList<PreviewImage> Textures(GameFile file, out string? warning, CancellationToken ct = default)
    {
        warning = null;
        if (!file.IsUePackage)
        {
            if (file.Extension.ToLowerInvariant() is "png" or "jpg" or "jpeg" or "bmp" or "gif" or "webp")
                return [new PreviewImage(file.NameWithoutExtension, file.Read())];
            throw new HeadlessException($"'{file.Path}' is neither a package nor an image");
        }
        var exports = AssetPreview.LoadExports(Archive.Provider, file, ct);
        return AssetPreview.EncodeTextures(exports.OfType<UTexture>(), Settings.CurrentVersioning.PlatformValue, ct, out warning);
    }

    private readonly Lock _soundsLock = new();
    private SoundExtractor? _sounds;

    /// <summary>The audio player's extractor (one extraction at a time: lock on it).</summary>
    public SoundExtractor Sounds
    {
        get
        {
            lock (_soundsLock) return _sounds ??= new SoundExtractor(Archive.Provider, Settings.ArchiveDir!);
        }
    }

    public void Dispose() => Archive.Dispose();

    // Progress<T> posts to the thread pool; the loader's labels only matter to the app's status bar
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
