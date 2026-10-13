using System.Diagnostics;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using Serilog;

namespace FModelMac.Services;

public sealed record LoadOptions(
    string ArchiveDir,
    EGame Game,
    string? AesKey,
    IReadOnlyDictionary<string, string> DynamicKeys,
    string? UsmapPath);

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
            new VersionContainer(options.Game), StringComparer.OrdinalIgnoreCase);
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
    public FolderIndex BuildIndex(IReadOnlySet<IVfsReader>? scope)
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
        return new FolderIndex(folders, seen.Count);
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

    public void Dispose() => Provider.Dispose();
}
