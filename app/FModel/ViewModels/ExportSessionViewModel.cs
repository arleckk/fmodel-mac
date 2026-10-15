using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using FModel.Services;
using Serilog.Events;

namespace FModel.ViewModels;

public enum ExportItemState { Queued, Exporting, Exported, Failed }

/// <summary>One log line of an exporter (FModel's LogEntryViewModel).</summary>
public sealed record ExportLogEntry(LogEventLevel Level, DateTimeOffset Time, string Message, string? Details)
{
    public bool IsExpanded { get; set; } // TreeViewItem binds it on every level
    public string LevelText => Level switch
    {
        LogEventLevel.Fatal or LogEventLevel.Error => "ERR",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Information => "INF",
        _ => "DBG"
    };
    public bool IsError => Level >= LogEventLevel.Error;
    public bool IsWarning => Level == LogEventLevel.Warning;
}

/// <summary>
/// Where a queued asset lives: its package and export index, read from the export map without loading it. The asset
/// itself is only loaded when its batch runs, so a queue of a whole game doesn't hold the game in memory.
/// </summary>
public sealed record ExportSource(GameFile Package, int Index, string Name, string ObjectPath, string ClassName, Type Type);

/// <summary>
/// The exports of a package that a filter accepts, found from its export map: each class is constructed once (as
/// loading the export would) to run the filter, and nothing is deserialized.
/// </summary>
public sealed class ExportFinder(IFileProvider provider, Func<UObject, bool> filter)
{
    // Class path → filter result and type; only the result is kept, a dummy object would keep its package alive
    private readonly ConcurrentDictionary<string, (bool Match, Type Type)> _classes = new(StringComparer.OrdinalIgnoreCase);

    public ExportSource[] Find(GameFile file)
    {
        var package = provider.LoadPackage(file);
        if (package is not AbstractUePackage owner) return [];
        // The path CUE4Parse's exporters report: the fixed package path plus the object name
        var packagePath = provider.FixPath(package.Name).SubstringBeforeLast('.');
        var found = new List<ExportSource>();
        for (var i = 0; i < package.ExportsLazy.Length; i++)
        {
            if (new FPackageIndex(package, i + 1).ResolvedObject is not { } export) continue;
            var cls = export.Class;
            var (match, type) = _classes.GetOrAdd(cls?.GetPathName() ?? "", _ =>
            {
                var dummy = owner.ConstructObject(cls, package);
                return (filter(dummy), dummy.GetType());
            });
            if (!match) continue;
            var name = export.Name.Text;
            found.Add(new ExportSource(file, i, name, $"{packagePath}.{name}", cls?.Name.Text ?? type.Name, type));
        }
        return found.ToArray();
    }
}

/// <summary>A queued asset or package (raw data), or a dependency (material, texture) the session exported along with it.</summary>
public sealed partial class ExportItem(string objectPath, string className, ExportSource? source, GameFile? rawFile = null,
    IFileProvider? provider = null) : ObservableObject
{
    public ExportSource? Source { get; } = source;
    /// <summary>Export › Raw Data: the package's files as they are in the archive.</summary>
    public GameFile? RawFile { get; } = rawFile;
    public IFileProvider? Provider { get; } = provider;
    public string ObjectPath { get; } = objectPath;
    public string ClassName { get; } = className;
    public string Name { get; } = objectPath.SubstringAfterLast('.');
    public string Directory { get; } = objectPath.SubstringBeforeLast('.').SubstringBeforeLast('/');
    public bool IsDependency => Source is null && RawFile is null;
    public ObservableCollection<ExportLogEntry> Logs { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText), nameof(CanRemove))]
    private ExportItemState _state = ExportItemState.Queued;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFile), nameof(StateText))]
    private string? _firstFile;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateText))]
    private int _fileCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private bool _isExpanded;

    public bool HasFile => FirstFile is not null;
    public bool CanRemove => State == ExportItemState.Queued && !IsDependency;
    public string StateText => State switch
    {
        ExportItemState.Queued => IsDependency ? "dependency" : "queued",
        ExportItemState.Exporting => "exporting…",
        ExportItemState.Exported => FileCount == 1 ? Path.GetExtension(FirstFile)?.TrimStart('.') ?? "1 file" : $"{FileCount} files",
        _ => "failed"
    };
}

/// <summary>Assets of one class (FModel's ClassGroupViewModel).</summary>
public sealed partial class ExportClassGroup(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ObservableCollection<ExportItem> Items { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasErrors))]
    private int _errorCount;
    public bool HasErrors => ErrorCount > 0;
    [ObservableProperty] private bool _isExpanded = true;
}

public sealed record ExportSummary(IReadOnlyList<string> Files, int Failed, bool Canceled, TimeSpan Elapsed, string Directory);

/// <summary>
/// FModel's Export Session: Export › Model/Animation/World/Texture queue assets here, and they're exported together
/// with progress, ETA and per-asset logs. CUE4Parse's ExportSession takes one set of options per run, so the queue
/// runs in groups: worlds (USD only) and, with glTF, animations (USD) get their own run. The queue only holds where
/// each asset is; a run loads and exports them a batch at a time.
/// </summary>
public sealed partial class ExportSessionViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<ExportClassGroup> Groups { get; } = [];
    private readonly Dictionary<string, ExportItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _classNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanExport), nameof(CanClear))]
    private bool _isRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanExport))]
    private int _queuedCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _succeededCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _currentItem;
    [ObservableProperty] private string _elapsedText = "00:00.00";
    [ObservableProperty] private string _etaText = "--:--";
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private bool _isCanceled;

    /// <summary>FModel's "Override default export options for this session".</summary>
    [ObservableProperty] private bool _overrideOptions;
    public ExportOptionValues? OverrideValues { get; set; }
    public string? OverrideDirectory { get; set; }

    public bool CanExport => !IsRunning && QueuedCount > 0;

    /// <summary>
    /// How many queued assets are loaded and exported at a time: memory follows the batch, not the queue (a texture
    /// holds its mips, a mesh its LODs), so a whole game can go through one session.
    /// </summary>
    public int BatchSize { get; set; } = 256;

    /// <summary>
    /// Shows FModel's Streaming Levels dialog for a world (on the UI thread) and completes when it's closed. Without
    /// it, worlds export with CUE4Parse's default choice (only the always-loaded levels).
    /// </summary>
    public Func<StreamingLevelFilterViewModel, Task>? ChooseStreamingLevels { get; set; }

    // Called by CUE4Parse on an exporter thread, one world at a time: wait for the user, with the clock paused
    private void FilterStreamingLevels(StreamingLevelFilterArgs args, CancellationToken ct)
    {
        if (ChooseStreamingLevels is not { } choose) return;
        var done = new TaskCompletionSource();
        void Ask()
        {
            try
            {
                choose(new StreamingLevelFilterViewModel(args)).ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default);
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }
        _stopwatch.Stop();
        try
        {
            if (_context is null) Ask();
            else _context.Post(_ => Ask(), null);
            done.Task.Wait(ct);
        }
        finally
        {
            _stopwatch.Start();
        }
    }
    public bool CanClear => !IsRunning;

    public ExportSessionViewModel() => ExportLogSink.Received += OnLog;

    /// <summary>Stops listening to exporter logs (sessions created for one run, like the command line's).</summary>
    public void Dispose() => ExportLogSink.Received -= OnLog;

    /// <summary>Queues exports (found with <see cref="ExportFinder"/>); returns how many were new.</summary>
    public int Enqueue(IEnumerable<ExportSource> exports, IFileProvider provider)
    {
        if (IsRunning) return 0;
        if (QueuedCount == 0) ClearHistory(); // a new batch after a finished run starts clean, like FModel
        var added = 0;
        foreach (var export in exports)
        {
            var path = export.ObjectPath;
            if (_items.TryGetValue(path, out var existing) && existing.State == ExportItemState.Queued) continue;
            if (existing is not null) RemoveItem(existing);
            AddItem(new ExportItem(path, export.ClassName, export, null, provider));
            added++;
        }
        QueuedCount = _items.Values.Count(i => i.State == ExportItemState.Queued && !i.IsDependency);
        return added;
    }

    /// <summary>FModel's Export › Raw Data: the package files (.uasset, .uexp, .ubulk…) written as they are.</summary>
    public int EnqueueRaw(IEnumerable<GameFile> files, IFileProvider provider)
    {
        if (IsRunning) return 0;
        if (QueuedCount == 0) ClearHistory();
        var added = 0;
        foreach (var file in files)
        {
            var path = $"{file.PathWithoutExtension}.{file.NameWithoutExtension}"; // RawDataExporter's ObjectPath
            if (_items.TryGetValue(path, out var existing) && existing.State == ExportItemState.Queued) continue;
            if (existing is not null) RemoveItem(existing);
            AddItem(new ExportItem(path, RawDataClass, null, file, provider));
            added++;
        }
        QueuedCount = _items.Values.Count(i => i.State == ExportItemState.Queued && !i.IsDependency);
        return added;
    }

    public const string RawDataClass = "RawData";

    public void Remove(ExportItem item)
    {
        if (!item.CanRemove || IsRunning) return;
        RemoveItem(item);
        QueuedCount = _items.Values.Count(i => i.State == ExportItemState.Queued && !i.IsDependency);
    }

    public void Clear()
    {
        if (IsRunning) return;
        ClearHistory();
        Groups.Clear();
        _items.Clear();
        QueuedCount = 0;
    }

    public void Cancel() => _cts?.Cancel();

    public async Task<ExportSummary> RunAsync(AppSettings settings, string defaultDirectory)
    {
        var values = OverrideOptions && OverrideValues is not null ? OverrideValues : ExportOptionValues.FromSettings(settings);
        var directory = OverrideOptions && !string.IsNullOrWhiteSpace(OverrideDirectory) ? OverrideDirectory : defaultDirectory;
        var platform = settings.CurrentVersioning.PlatformValue;
        var queued = _items.Values.Where(i => i.State == ExportItemState.Queued && !i.IsDependency).ToArray();
        var files = new List<string>();
        if (queued.Length == 0 || IsRunning) return new ExportSummary(files, 0, false, TimeSpan.Zero, directory);

        IsRunning = true;
        IsFinished = IsCanceled = false;
        CompletedCount = SucceededCount = FailedCount = 0;
        Progress = 0;
        var cts = _cts = new CancellationTokenSource();
        _stopwatch.Restart();

        // CUE4Parse can't write animations or lone skeletons to glTF, and worlds only to USD
        EMeshFormat FormatOf(ExportItem item) => item.Source?.Type switch
        {
            { } type when type.IsAssignableTo(typeof(UWorld)) => EMeshFormat.USD,
            { } type when values.MeshFormat == EMeshFormat.Gltf2 &&
                          (type.IsAssignableTo(typeof(UAnimationAsset)) || type.IsAssignableTo(typeof(USkeleton))) => EMeshFormat.USD,
            _ => values.MeshFormat
        };

        // One CUE4Parse run per mesh format, in batches of BatchSize loaded just before they're exported
        var batches = queued.GroupBy(FormatOf).SelectMany(run => run.Chunk(Math.Max(1, BatchSize)).Select(items => (Format: run.Key, Items: items))).ToArray();
        var remaining = queued.Length;
        var done = 0;
        try
        {
            for (var b = 0; b < batches.Length; b++)
            {
                var (format, items) = batches[b];
                remaining -= items.Length;
                var (exports, unreadable) = await Task.Run(() => Load(items, cts.Token), cts.Token);
                foreach (var (item, error) in unreadable)
                {
                    lock (_items) Apply(ExportResult.Failure(item.ObjectPath, error));
                }
                done += unreadable.Count;
                CompletedCount = Math.Max(CompletedCount, done);
                var ready = items.Where(i => i.RawFile is not null || exports.ContainsKey(i)).ToArray();
                if (ready.Length > 0)
                {
                    var results = await RunGroupAsync(ready, exports, values.ToOptions(platform, format), directory, done, remaining, cts.Token);
                    done += results.Count;

                    // CUE4Parse can't always read Nanite clusters: "Nanite Only" then leaves the mesh without LODs, so
                    // export those again from the fallback mesh every Nanite mesh carries
                    if (values.NaniteFormat != ENaniteMeshFormat.NoNanite)
                    {
                        var retry = ready.Where(i => i.State == ExportItemState.Failed && i.Error?.EndsWith("has no LODs") == true).ToArray();
                        if (retry.Length > 0)
                        {
                            foreach (var item in retry)
                            {
                                item.Logs.Add(new ExportLogEntry(LogEventLevel.Warning, DateTimeOffset.Now,
                                    "Couldn't read the Nanite data: exporting the fallback mesh instead", null));
                                ClearErrors(item); // the error stays in the log, but it's handled
                                FailedCount--;
                                CompletedCount--;
                            }
                            await RunGroupAsync(retry, exports, values.ToOptions(platform, format, ENaniteMeshFormat.NoNanite), directory,
                                done - retry.Length, remaining, cts.Token);
                        }
                    }
                }
                exports.Clear(); // an async method's locals live on: don't keep this batch while the next one loads
                if (batches.Length > 1)
                    Serilog.Log.Information("Export Session: batch {Batch}/{Batches} done ({Count} assets), {Managed:N0} MB managed, {WorkingSet:N0} MB in use",
                        b + 1, batches.Length, items.Length, GC.GetTotalMemory(false) >> 20, Environment.WorkingSet >> 20);
            }
        }
        catch (OperationCanceledException)
        {
            IsCanceled = true;
        }
        finally
        {
            _stopwatch.Stop();
            UpdateClock();
            EtaText = "--:--";
            foreach (var item in _items.Values.Where(i => i.State == ExportItemState.Exporting)) item.State = ExportItemState.Queued;
            QueuedCount = _items.Values.Count(i => i.State == ExportItemState.Queued && !i.IsDependency);
            CurrentItem = null;
            IsRunning = false;
            IsFinished = !IsCanceled;
            _cts = null;
        }

        files.AddRange(_files.Values.SelectMany(f => f));
        return new ExportSummary(files, FailedCount, IsCanceled, _stopwatch.Elapsed, directory);
    }

    private readonly Dictionary<string, IReadOnlyList<string>> _files = new(StringComparer.OrdinalIgnoreCase);

    // A batch's assets, read from their packages (each package once, in parallel); the items of a package that
    // can't be read, or no longer has the export, fail with the reason
    private static (Dictionary<ExportItem, UObject> Exports, List<(ExportItem Item, Exception Error)> Unreadable) Load(
        ExportItem[] items, CancellationToken ct)
    {
        var exports = new ConcurrentDictionary<ExportItem, UObject>();
        var unreadable = new ConcurrentQueue<(ExportItem, Exception)>();
        var packages = items.Where(i => i.Source is not null).GroupBy(i => i.Source!.Package).ToArray();
        Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct }, group =>
        {
            try
            {
                var package = group.First().Provider!.LoadPackage(group.Key);
                foreach (var item in group)
                {
                    var source = item.Source!;
                    var export = package.GetExport(source.Index) is { } byIndex && byIndex.Name == source.Name
                        ? byIndex
                        : package.GetExportOrNull(source.Name);
                    if (export is not null) exports[item] = export;
                    else unreadable.Enqueue((item, new KeyNotFoundException($"{group.Key.Path} has no export named '{source.Name}'")));
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not read {Path}", group.Key.Path);
                foreach (var item in group) unreadable.Enqueue((item, e));
            }
        });
        // Back in queue order, so the failures read like the rest of the run
        return (new Dictionary<ExportItem, UObject>(exports), unreadable.OrderBy(u => Array.IndexOf(items, u.Item1)).ToList());
    }

    private async Task<IReadOnlyList<ExportResult>> RunGroupAsync(ExportItem[] items, IReadOnlyDictionary<ExportItem, UObject> exports,
        ExportOptions options, string directory, int doneBefore, int queuedAfter, CancellationToken ct)
    {
        var session = new ExportSession(ChooseStreamingLevels is null ? null : FilterStreamingLevels);
        foreach (var item in items)
        {
            if (item.RawFile is { } file) session.Add(new RawDataExporter(file, item.Provider!));
            else session.Add(exports[item]);
            item.State = ExportItemState.Exporting;
        }
        // Progress reports are posted, so some can land after RunAsync returns: each result is applied once,
        // from whichever comes first
        var applied = new HashSet<ExportResult>();
        void ApplyOnce(ExportResult result)
        {
            lock (_items)
            {
                if (applied.Add(result)) Apply(result);
            }
        }
        var progress = new Progress<ExportProgress>(p =>
        {
            var total = doneBefore + p.Total + queuedAfter;
            if (IsRunning)
            {
                CompletedCount = Math.Max(CompletedCount, doneBefore + p.Completed);
                Progress = total > 0 ? (double) CompletedCount / total : 0;
                UpdateClock(total);
            }
            if (p.LastResult is not { } result) return;
            if (IsRunning) CurrentItem = result.ObjectPath;
            ApplyOnce(result);
        });
        var results = await session.RunAsync(directory, options, progress, ct);
        foreach (var result in results) ApplyOnce(result);
        return results;
    }

    private void Apply(ExportResult result)
    {
        if (!_items.TryGetValue(result.ObjectPath, out var item))
        {
            // A material or texture the session exported along with a mesh
            item = new ExportItem(result.ObjectPath, _classNames.GetValueOrDefault(result.ObjectPath) ?? "Dependencies", null);
            AddItem(item);
        }
        if (result.Success)
        {
            item.State = ExportItemState.Exported;
            item.Error = null;
            _files[item.ObjectPath] = result.DiskFilePaths ?? [];
            item.FirstFile = result.DiskFilePaths?.FirstOrDefault();
            item.FileCount = result.DiskFilePaths?.Count ?? 0;
            SucceededCount++;
        }
        else
        {
            item.State = ExportItemState.Failed;
            item.Error = result.Error?.Message ?? "Export failed";
            FailedCount++;
            if (!item.Logs.Any(l => l.IsError))
            {
                item.Logs.Add(new ExportLogEntry(LogEventLevel.Error, DateTimeOffset.Now, item.Error, result.Error?.ToString()));
                CountError(item);
            }
        }
    }

    // Exporter logs arrive on worker threads: hand them to the UI thread
    private void OnLog(LogEvent log)
    {
        if (ExportLogSink.Property(log, "ObjectPath") is not { } path) return;
        var entry = new ExportLogEntry(log.Level, log.Timestamp, log.Exception?.Message ?? log.RenderMessage(), log.Exception?.ToString());
        var className = ExportLogSink.Property(log, "ClassName");
        void Add()
        {
            if (className is not null) _classNames.TryAdd(path, className);
            if (!_items.TryGetValue(path, out var item))
            {
                if (!IsRunning) return; // "Queued for export" and other logs outside a run
                item = new ExportItem(path, className ?? "Dependencies", null);
                AddItem(item);
            }
            item.Logs.Add(entry);
            if (entry.IsError) CountError(item);
        }
        if (_context is null)
        {
            lock (_items) Add(); // no UI thread (tools): logs arrive from the exporters' worker threads
        }
        else _context.Post(_ => Add(), null);
    }

    private void ClearErrors(ExportItem item)
    {
        if (Groups.FirstOrDefault(g => g.Items.Contains(item)) is { } group) group.ErrorCount -= item.ErrorCount;
        item.ErrorCount = 0;
        item.Error = null;
    }

    private void CountError(ExportItem item)
    {
        item.ErrorCount++;
        if (Groups.FirstOrDefault(g => g.Items.Contains(item)) is { } group) group.ErrorCount++;
    }

    private void AddItem(ExportItem item)
    {
        _items[item.ObjectPath] = item;
        var group = Groups.FirstOrDefault(g => g.Name == item.ClassName);
        if (group is null)
        {
            group = new ExportClassGroup(item.ClassName);
            Groups.Add(group);
        }
        group.Items.Add(item);
    }

    private void RemoveItem(ExportItem item)
    {
        _items.Remove(item.ObjectPath);
        if (Groups.FirstOrDefault(g => g.Items.Contains(item)) is not { } group) return;
        group.Items.Remove(item);
        if (group.Items.Count == 0) Groups.Remove(group);
    }

    private void ClearHistory()
    {
        foreach (var done in _items.Values.Where(i => i.State != ExportItemState.Queued || i.IsDependency).ToArray()) RemoveItem(done);
        _files.Clear();
        _classNames.Clear();
        CompletedCount = SucceededCount = FailedCount = 0;
        Progress = 0;
        _stopwatch.Reset();
        ElapsedText = "00:00.00";
        EtaText = "--:--";
        CurrentItem = null;
        IsFinished = IsCanceled = false;
    }

    /// <summary>Elapsed time and ETA; the window also calls it on a timer while running.</summary>
    public void UpdateClock(int? total = null)
    {
        var elapsed = _stopwatch.Elapsed;
        ElapsedText = $"{(int) elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}";
        if (total is not { } t || !IsRunning || CompletedCount < 2 || elapsed.TotalSeconds <= 0) return;
        var rate = CompletedCount / elapsed.TotalSeconds;
        var eta = TimeSpan.FromSeconds(Math.Max(0, t - CompletedCount) / rate);
        EtaText = $"{(int) eta.TotalMinutes:00}:{eta.Seconds:00}";
    }
}
