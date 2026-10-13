using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.Utils;
using FModelMac.Services;
using Serilog.Events;

namespace FModelMac.ViewModels;

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

/// <summary>A queued asset, or a dependency (material, texture) the session exported along with it.</summary>
public sealed partial class ExportItem(string objectPath, string className, UObject? export) : ObservableObject
{
    public UObject? Export { get; } = export;
    public string ObjectPath { get; } = objectPath;
    public string ClassName { get; } = className;
    public string Name { get; } = objectPath.SubstringAfterLast('.');
    public string Directory { get; } = objectPath.SubstringBeforeLast('.').SubstringBeforeLast('/');
    public bool IsDependency => Export is null;
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
/// runs in groups: worlds (USD only) and, with glTF, animations (USD) get their own run.
/// </summary>
public sealed partial class ExportSessionViewModel : ObservableObject
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
    public bool CanClear => !IsRunning;

    public ExportSessionViewModel() => ExportLogSink.Received += OnLog;

    /// <summary>Queues exports; returns how many were new.</summary>
    public int Enqueue(IEnumerable<UObject> exports, IFileProvider provider)
    {
        if (IsRunning) return 0;
        if (QueuedCount == 0) ClearHistory(); // a new batch after a finished run starts clean, like FModel
        var added = 0;
        foreach (var export in exports)
        {
            var path = ObjectPath(export, provider);
            if (_items.TryGetValue(path, out var existing) && existing.State == ExportItemState.Queued) continue;
            if (existing is not null) RemoveItem(existing);
            AddItem(new ExportItem(path, export.ExportType, export));
            added++;
        }
        QueuedCount = _items.Values.Count(i => i.State == ExportItemState.Queued && !i.IsDependency);
        return added;
    }

    // The path CUE4Parse's exporters report: the fixed package path plus the object name
    public static string ObjectPath(UObject export, IFileProvider provider) => export.Owner is { } owner
        ? $"{provider.FixPath(owner.Name).SubstringBeforeLast('.')}.{export.Name}"
        : export.GetPathName();

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
        var queued = _items.Values.Where(i => i.State == ExportItemState.Queued && i.Export is not null).ToArray();
        var files = new List<string>();
        if (queued.Length == 0 || IsRunning) return new ExportSummary(files, 0, false, TimeSpan.Zero, directory);

        IsRunning = true;
        IsFinished = IsCanceled = false;
        CompletedCount = SucceededCount = FailedCount = 0;
        Progress = 0;
        var cts = _cts = new CancellationTokenSource();
        _stopwatch.Restart();

        // CUE4Parse can't write animations to glTF, and worlds only to USD
        EMeshFormat FormatOf(UObject export) => export switch
        {
            UWorld => EMeshFormat.USD,
            UAnimationAsset when values.MeshFormat == EMeshFormat.Gltf2 => EMeshFormat.USD,
            _ => values.MeshFormat
        };

        var runs = queued.GroupBy(i => FormatOf(i.Export!)).ToArray();
        var remaining = queued.Length;
        var done = 0;
        try
        {
            foreach (var run in runs)
            {
                var items = run.ToArray();
                remaining -= items.Length;
                var results = await RunGroupAsync(items, values.ToOptions(platform, run.Key), directory, done, remaining, cts.Token);
                done += results.Count;

                // CUE4Parse can't always read Nanite clusters: "Nanite Only" then leaves the mesh without LODs, so
                // export those again from the fallback mesh every Nanite mesh carries
                if (values.NaniteFormat != ENaniteMeshFormat.NoNanite)
                {
                    var retry = items.Where(i => i.State == ExportItemState.Failed && i.Error?.EndsWith("has no LODs") == true).ToArray();
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
                        await RunGroupAsync(retry, values.ToOptions(platform, run.Key, ENaniteMeshFormat.NoNanite), directory,
                            done - retry.Length, remaining, cts.Token);
                    }
                }
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

    private async Task<IReadOnlyList<ExportResult>> RunGroupAsync(ExportItem[] items, ExportOptions options, string directory,
        int doneBefore, int queuedAfter, CancellationToken ct)
    {
        var session = new ExportSession();
        foreach (var item in items)
        {
            session.Add(item.Export!);
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
