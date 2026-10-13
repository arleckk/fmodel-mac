using System.Collections.Concurrent;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using FModel.Services;
using FModel.Services.Audio;
using FModel.ViewModels;
using Newtonsoft.Json;

namespace FModel.Headless;

/// <summary>The app's Export menu (and Export Folder).</summary>
public enum ExportKind { Properties, Raw, Textures, Models, Animations, Worlds, Audio, Code }

/// <param name="Empty">Files that had nothing of the kind (a texture export of a sound, say).</param>
public sealed record ExportReport(string Directory, IReadOnlyList<string> Files, IReadOnlyList<string> Errors, int Empty);

/// <summary>
/// Exports files the way the app's Export menu does, with its export settings (Settings › Export) and into its
/// output folder (Output/Exports, keeping the directory structure): properties and decompiled blueprints are written
/// directly, sounds through the audio player's extractor, the rest through the Export Session.
/// </summary>
public static class HeadlessExport
{
    public static readonly IReadOnlyDictionary<string, ExportKind> Names = new Dictionary<string, ExportKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["properties"] = ExportKind.Properties, ["json"] = ExportKind.Properties,
        ["raw"] = ExportKind.Raw,
        ["textures"] = ExportKind.Textures, ["texture"] = ExportKind.Textures,
        ["models"] = ExportKind.Models, ["model"] = ExportKind.Models,
        ["animations"] = ExportKind.Animations, ["animation"] = ExportKind.Animations,
        ["worlds"] = ExportKind.Worlds, ["world"] = ExportKind.Worlds,
        ["audio"] = ExportKind.Audio, ["sounds"] = ExportKind.Audio,
        ["code"] = ExportKind.Code, ["blueprints"] = ExportKind.Code
    };

    public const string KindList = "properties, raw, textures, models, animations, worlds, audio, code";

    public static ExportKind ParseKind(string kind) => Names.TryGetValue(kind.Trim(), out var value)
        ? value
        : throw new HeadlessException($"Unknown export kind '{kind}': use one of {KindList}");

    public static async Task<ExportReport> RunAsync(HeadlessGame game, IReadOnlyList<GameFile> files, ExportKind kind,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var settings = game.Settings;
        return kind switch
        {
            ExportKind.Properties => await WriteEachAsync(game, files, (file, path) =>
            {
                var target = path + ".json";
                File.WriteAllText(target, game.Properties(file, ct: ct));
                return target;
            }, ct),
            ExportKind.Code => await WriteEachAsync(game, files, (file, path) =>
            {
                if (game.Archive.DecompileBlueprint(file) is not { } code) return null;
                var target = path + ".cpp";
                File.WriteAllText(target, code);
                return target;
            }, ct),
            ExportKind.Audio => await Task.Run(() => ExportAudio(game, files, ct), ct),
            ExportKind.Raw => await RunSessionAsync(game, (session, _) => session.EnqueueRaw(files.Where(f => f.IsUePackage || !f.IsUePackagePayload), game.Archive.Provider), log, ct),
            _ => await RunSessionAsync(game, (session, errors) =>
            {
                Func<UObject, bool> filter = kind switch
                {
                    ExportKind.Textures => e => e is UTexture,
                    ExportKind.Models => e => e is UStaticMesh or USkinnedAsset || settings.SaveSkeletonAsMesh && e is USkeleton,
                    ExportKind.Animations => e => e is UAnimationAsset,
                    _ => e => e is UWorld
                };
                var exports = ReadExports(game, files, filter, errors, ct);
                return session.Enqueue(exports, game.Archive.Provider);
            }, log, ct)
        };
    }

    // Properties and code: one file per package, in parallel like the app's Export Folder
    private static Task<ExportReport> WriteEachAsync(HeadlessGame game, IReadOnlyList<GameFile> files, Func<GameFile, string, string?> export,
        CancellationToken ct) => Task.Run(() =>
    {
        var written = new ConcurrentBag<string>();
        var errors = new ConcurrentQueue<string>();
        var empty = 0;
        Parallel.ForEach(files.Where(f => f.IsUePackage), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, file =>
        {
            try
            {
                var basePath = game.Settings.ExportPath(file.PathWithoutExtension);
                Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                if (export(file, basePath) is { } target) written.Add(target);
                else Interlocked.Increment(ref empty);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Serilog.Log.Error(e, "Export failed for {Path}", file.Path);
                errors.Enqueue($"{file.Path}: {e.Message}");
            }
        });
        empty += files.Count(f => !f.IsUePackage);
        return new ExportReport(game.Settings.ExportsDir, written.Order(StringComparer.Ordinal).ToArray(), errors.ToArray(), empty);
    }, ct);

    private static ExportReport ExportAudio(HeadlessGame game, IReadOnlyList<GameFile> files, CancellationToken ct)
    {
        var written = new List<string>();
        var errors = new List<string>();
        var empty = 0;
        var sounds = game.Sounds;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                List<ExtractedSound> found;
                lock (sounds)
                {
                    found = file.IsUePackage
                        ? sounds.FromExports(file, AssetPreview.LoadExports(game.Archive.Provider, file, ct))
                        : SoundExtractor.AudioExtensions.Contains(file.Extension) ? sounds.FromFile(file, out _) : [];
                    errors.AddRange(sounds.Warnings.Select(w => $"{file.Path}: {w}"));
                }
                if (found.Count == 0) empty++;
                foreach (var sound in found)
                {
                    try
                    {
                        written.Add(AudioExport.Save(sound, game.Settings));
                    }
                    catch (Exception e)
                    {
                        Serilog.Log.Warning(e, "Audio export failed for {Sound}", sound.FileName);
                        errors.Add($"{sound.FileName}: {e.Message}");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Serilog.Log.Error(e, "Audio export failed for {Path}", file.Path);
                errors.Add($"{file.Path}: {e.Message}");
            }
        }
        return new ExportReport(game.Settings.ExportsDir, written, errors, empty);
    }

    // The exports of each package that pass the filter, read in parallel; unreadable packages are reported and skipped
    private static UObject[] ReadExports(HeadlessGame game, IReadOnlyList<GameFile> files, Func<UObject, bool> filter,
        ConcurrentQueue<string> errors, CancellationToken ct)
    {
        var packages = files.Where(f => f.IsUePackage).ToArray();
        var found = new ConcurrentBag<(int Index, UObject[] Exports)>();
        Parallel.For(0, packages.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, i =>
        {
            try
            {
                var matching = AssetPreview.LoadExports(game.Archive.Provider, packages[i], ct).Where(filter).ToArray();
                if (matching.Length > 0) found.Add((i, matching));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Serilog.Log.Warning(e, "Could not read {Path}", packages[i].Path);
                errors.Enqueue($"{packages[i].Path}: {e.Message}");
            }
        });
        return found.OrderBy(f => f.Index).SelectMany(f => f.Exports).ToArray();
    }

    // Models, animations, worlds, textures and raw data: the Export Session, with its per-asset results
    private static async Task<ExportReport> RunSessionAsync(HeadlessGame game, Func<ExportSessionViewModel, ConcurrentQueue<string>, int> enqueue,
        Action<string>? log, CancellationToken ct)
    {
        using var session = new ExportSessionViewModel();
        var errors = new ConcurrentQueue<string>();
        var queued = await Task.Run(() => enqueue(session, errors), ct);
        if (queued == 0) return new ExportReport(game.Settings.ExportsDir, [], errors.ToArray(), 0);

        log?.Invoke($"Exporting {queued} asset(s)");
        using var cancel = ct.Register(session.Cancel);
        var summary = await session.RunAsync(game.Settings, game.Settings.ExportsDir);
        ct.ThrowIfCancellationRequested();
        foreach (var item in session.Groups.SelectMany(g => g.Items).Where(i => i.State == ExportItemState.Failed))
            errors.Enqueue($"{item.ObjectPath}: {item.Error}");
        return new ExportReport(summary.Directory, summary.Files.Distinct(StringComparer.Ordinal).ToArray(), errors.ToArray(), 0);
    }
}
