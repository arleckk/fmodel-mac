using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace FModelMac.Services;

/// <summary>What the 3D viewer needs from the loaded game to list, play and voice a mesh's animations.</summary>
public interface IAnimationSource
{
    Task<IReadOnlyList<AnimationEntry>> FindAnimationsAsync(Rendering.ViewerModel model, Action<IReadOnlyList<AnimationEntry>>? partial,
        IProgress<double>? indexProgress, CancellationToken ct);
    Task<Rendering.IViewerMotion> LoadMotionAsync(GameFile file);
    Task<Audio.PcmAudio?> DecodeNotifySoundAsync(string path);
}

/// <summary>An animation asset found for a skeleton.</summary>
/// <param name="Kind">Montage, Sequence, Composite, Blend Space or Aim Offset.</param>
public sealed record AnimationEntry(GameFile File, string Name, string Kind)
{
    public int Order => Kind switch { "Montage" => 0, "Sequence" => 1, "Composite" => 2, "Blend Space" => 3, _ => 4 };
}

/// <summary>
/// The animations made for a mesh, in any game: every animation asset points at the skeleton it was authored on, so
/// the packages that reference the mesh's skeleton (or a compatible one) are its animations, once their main export
/// is told apart from the rest (the mesh itself, physics assets, anim blueprints…) by reading the header alone.
/// </summary>
public sealed class AnimationLibrary(GameArchive archive)
{
    private readonly Dictionary<string, IReadOnlyList<AnimationEntry>> _found = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, GameFile>? _byName;

    private IFileProvider Provider => archive.Provider;

    /// <summary>
    /// The animations of these skeletons (object paths). <paramref name="partial"/> gets a first answer from the folders
    /// around the skeleton while a pak game's reference index is still being built (once, then kept on disk).
    /// </summary>
    public IReadOnlyList<AnimationEntry> Find(IReadOnlyList<string> skeletons, Action<IReadOnlyList<AnimationEntry>>? partial,
        IProgress<double>? indexProgress, CancellationToken ct)
    {
        var key = string.Join("|", skeletons);
        lock (_found)
            if (_found.TryGetValue(key, out var cached)) return cached;

        var files = skeletons.Select(PackageOf).OfType<GameFile>().ToArray();
        if (files.Length == 0) return [];

        if (archive.NeedsReferenceIndex && partial is not null)
        {
            // The skeleton's folder and its parent's (Character/Skeleton next to Character/Animations), capped
            var near = files.SelectMany(f => Near(f.Path)).Distinct().ToArray();
            partial(Classify(files.SelectMany(f => archive.FindReferencesAmong(f, near, ct)), ct));
        }

        var all = Classify(files.SelectMany(f => archive.FindReferences(f, indexProgress, ct)), ct);
        lock (_found) _found[key] = all;
        return all;
    }

    private IEnumerable<GameFile> Near(string skeletonPath)
    {
        var folder = skeletonPath.SubstringBeforeLast('/');
        var parent = folder.Contains('/') ? folder.SubstringBeforeLast('/') : folder;
        var under = Provider.Files.Values.Where(f => f.IsUePackage && f.Path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)).Take(20_001).ToArray();
        return under.Length <= 20_000 ? under : under.Where(f => f.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<AnimationEntry> Classify(IEnumerable<GameFile> files, CancellationToken ct)
    {
        var unique = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new AnimationEntry?[unique.Length];
        Parallel.For(0, unique.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var kind = MainExportClass(unique[i]) switch
            {
                UAnimMontage => "Montage",
                UAnimComposite => "Composite",
                UAimOffsetBlendSpace or UAimOffsetBlendSpace1D => "Aim Offset",
                UBlendSpaceBase => "Blend Space",
                UAnimSequence => "Sequence",
                _ => null
            };
            if (kind is not null) entries[i] = new AnimationEntry(unique[i], unique[i].NameWithoutExtension, kind);
        });
        return entries.OfType<AnimationEntry>()
            .OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>An empty object of the package's main export class (no data read), or null.</summary>
    public object? MainExportClass(GameFile file)
    {
        try
        {
            var package = Provider.LoadPackage(file);
            var main = package.GetExportIndex(file.NameWithoutExtension, StringComparison.OrdinalIgnoreCase);
            if (main < 0) main = 0;
            var pointer = new FPackageIndex(package, main + 1).ResolvedObject;
            return pointer is null || package is not AbstractUePackage abstractPackage ? null : abstractPackage.ConstructObject(pointer.Class, package);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The package file of an object path ("/Game/X/Y.Y", "Game/Content/X/Y.Y") or of a package path.</summary>
    public GameFile? PackageOf(string path)
    {
        var package = Provider.FixPath(path);
        if (package.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || package.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            package = package.SubstringBeforeLast('.');
        else if (package.LastIndexOf('.') > package.LastIndexOf('/')) package = package.SubstringBeforeLast('.');
        return Provider.TryGetGameFile(package + ".uasset", out var file) ? file : null;
    }

    /// <summary>
    /// For notifies that name their sound only by an event name: a package called that (or "name_Cue") whose main
    /// export is a sound asset, as its package path.
    /// </summary>
    public string? SoundByName(string name)
    {
        lock (this)
        {
            if (_byName is null)
            {
                _byName = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Provider.Files.Values)
                    if (file.IsUePackage) _byName.TryAdd(file.NameWithoutExtension, file);
            }
        }
        foreach (var candidate in new[] { name, name + "_Cue" })
        {
            if (!_byName.TryGetValue(candidate, out var file)) continue;
            if (MainExportClass(file) is CUE4Parse.UE4.Assets.Exports.UObject dummy && Rendering.NotifyReader.IsSound(dummy.ExportType))
                return file.PathWithoutExtension;
        }
        return null;
    }
}
