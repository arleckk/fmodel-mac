using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FModelMac.Rendering;
using FModelMac.Services;
using FModelMac.Services.Audio;

namespace FModelMac.Views;

// The ANIMATIONS list (the animations made for the selected mesh's skeleton) and the animation's notifies: marks on
// the timeline, their sounds played in sync, and markers where their effects spawn
public partial class ModelViewerWindow
{
    private static readonly Vector3 EffectColor = new(1f, 0.55f, 0.15f);
    private static readonly Vector3 SoundColor = new(0.4f, 0.85f, 0.5f);
    private static readonly IBrush EffectBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x26));
    private static readonly IBrush SoundBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xD9, 0x80));
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9C, 0xA8));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x1F, 0x26));

    // An instant notify's effect (or sound) stays marked this long, so it can be seen
    private const float MarkerSeconds = 0.6f;

    private readonly OneShotPlayer _notifySounds = new();
    private CancellationTokenSource? _animationSearch;
    private ViewerModel? _animationsModel;
    private IReadOnlyList<AnimationEntry> _animationEntries = [];
    private string _animationsStatus = "";
    private AnimationEntry? _playingEntry;
    private bool _fillingAnimations;
    private ViewerNotify[] _notifies = [];

    /// <summary>The loaded game, to list, play and voice the animations of a mesh (the main window sets it).</summary>
    public IAnimationSource? AnimationSource { get; set; }

    /// <summary>The animations listed for the selected mesh, and how many notify sounds played (test harness).</summary>
    public IReadOnlyList<AnimationEntry> AnimationEntries => _animationEntries;
    public int NotifySoundsPlayed => _notifySounds.Played;
    public IReadOnlyList<(string Label, Point? Position)> MarkerLabelPositions { get; private set; } = [];

    /// <summary>Raised once the ANIMATIONS list has its final content (test harness).</summary>
    public event Action? AnimationsListed;

    private void InitializeAnimations()
    {
        AnimationFilter.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) FillAnimations();
        };
        AnimationsList.SelectionChanged += async (_, _) =>
        {
            if (!_fillingAnimations && AnimationsList.SelectedItem is AnimationEntry entry) await PlayEntryAsync(entry);
        };
        NotifyStrip.SizeChanged += (_, _) => BuildNotifyStrip();
        Viewport.MarkersProjected += PlaceMarkerLabels;
        EffectsToggle.IsCheckedChanged += (_, _) => UpdateEffectMarkers();
        SoundsToggle.IsCheckedChanged += (_, _) =>
        {
            if (SoundsToggle.IsChecked != true) _notifySounds.StopAll();
        };
        Closed += (_, _) =>
        {
            _animationSearch?.Cancel();
            _notifySounds.Dispose();
        };
    }

    // ---------- The ANIMATIONS list ----------

    private async void ShowAnimationsFor(ViewerModel? model)
    {
        var show = model is { IsSkinned: true, SkeletonPath: not null } && AnimationSource is not null;
        AnimationsHeader.IsVisible = show;
        ListsGrid.RowDefinitions[4].Height = show ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        if (!show)
        {
            _animationSearch?.Cancel();
            _animationsModel = null;
            return;
        }
        if (model == _animationsModel) return;

        _animationsModel = model;
        _animationSearch?.Cancel();
        var search = _animationSearch = new CancellationTokenSource();
        var skeleton = model!.SkeletonPath!.Split('/', '.')[^1];
        var done = false;
        SetAnimations([], $"Looking for the animations of {skeleton}…");
        try
        {
            // A pak game indexes every package's imports the first time (then it's kept on disk); the folders around
            // the skeleton answer first
            var progress = new Progress<double>(p =>
            {
                if (!done && !search.IsCancellationRequested)
                    AnimationsStatus.Text = $"{_animationEntries.Count} found nearby · indexing the game's references, {p:P0} (only once)";
            });
            var entries = await AnimationSource!.FindAnimationsAsync(model, partial => Dispatcher.UIThread.Post(() =>
            {
                if (!done && !search.IsCancellationRequested)
                    SetAnimations(partial, $"{partial.Count} found nearby · indexing the game's references (only once)…");
            }), progress, search.Token);
            done = true;
            if (search.IsCancellationRequested) return;
            SetAnimations(entries, entries.Count == 0
                ? $"Nothing in the game references {skeleton}"
                : $"{entries.Count} animation(s) for {skeleton}" + (entries.Count > 1 ? " · click one to play it" : ""));
            AnimationsListed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not list the animations of {Skeleton}", model.SkeletonPath);
            AnimationsStatus.Text = $"Could not look for animations: {e.Message}";
            AnimationsListed?.Invoke();
        }
    }

    private void SetAnimations(IReadOnlyList<AnimationEntry> entries, string status)
    {
        _animationEntries = entries;
        _animationsStatus = status;
        AnimationsStatus.Text = status;
        FillAnimations();
    }

    private void FillAnimations()
    {
        var filter = AnimationFilter.Text?.Trim() ?? "";
        var shown = filter.Length == 0
            ? _animationEntries
            : _animationEntries.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        _fillingAnimations = true;
        AnimationsList.ItemsSource = shown;
        AnimationsList.SelectedItem = shown.FirstOrDefault(e => e.File.Path == _playingEntry?.File.Path);
        _fillingAnimations = false;
    }

    /// <summary>Plays one of the listed animations on the selected mesh (also used by the test harness).</summary>
    public async Task<string?> PlayEntryAsync(AnimationEntry entry)
    {
        if (AnimationSource is not { } source) return "No game loaded";
        AnimationsStatus.Text = $"Loading {entry.Name}…";
        string? error;
        try
        {
            var motion = await source.LoadMotionAsync(entry.File);
            error = PlayAnimation(motion);
            if (error is null)
            {
                _playingEntry = entry;
                _fillingAnimations = true;
                AnimationsList.SelectedItem = entry;
                _fillingAnimations = false;
            }
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not play {Animation}", entry.File.Path);
            error = $"{entry.Name}: {e.Message}";
        }
        AnimationsStatus.Text = error ?? _animationsStatus;
        return error;
    }

    // ---------- Notifies ----------

    private static string NotifySummary(ViewerAnimation animation)
    {
        var notifies = animation.Notifies;
        if (notifies.Length == 0) return "";
        var effects = notifies.Count(n => n.Kind == NotifyKind.Effect);
        var sounds = notifies.Count(n => n.Kind == NotifyKind.Sound);
        return $" · {notifies.Length} notify(s): {effects} effect(s), {sounds} sound(s)";
    }

    private void SetupNotifies(ViewerAnimation? animation)
    {
        _notifies = animation?.Notifies ?? [];
        _notifySounds.StopAll();
        NotifyStrip.IsVisible = _notifies.Length > 0;
        BuildNotifyStrip();
        // Decode the sounds ahead, so they play on time
        if (AnimationSource is { } source)
            foreach (var path in _notifies.Where(n => n.Kind == NotifyKind.Sound).Select(n => n.AssetPath).OfType<string>().Distinct())
                _ = source.DecodeNotifySoundAsync(path);
        UpdateEffectMarkers();
    }

    // A mark per notify under the time slider (as long as its duration for notify states), colored by kind
    private void BuildNotifyStrip()
    {
        NotifyStrip.Children.Clear();
        var width = NotifyStrip.Bounds.Width;
        if (_notifies.Length == 0 || _pose is null || width <= 0) return;
        var duration = _pose.Duration;
        foreach (var notify in _notifies)
        {
            var x = Math.Clamp(notify.Time / duration, 0, 1) * width;
            var w = Math.Max(3, notify.Duration / duration * width);
            var mark = new Rectangle
            {
                Width = w, Height = 8, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0.9, Cursor = new Cursor(StandardCursorType.Hand),
                Fill = notify.Kind switch { NotifyKind.Effect => EffectBrush, NotifyKind.Sound => SoundBrush, _ => OtherBrush }
            };
            ToolTip.SetTip(mark, notify.Description);
            mark.PointerPressed += (_, e) =>
            {
                SeekPhase(notify.Time / duration);
                e.Handled = true;
            };
            Canvas.SetLeft(mark, Math.Min(x - (notify.Duration > 0 ? 0 : 1.5), width - w));
            Canvas.SetTop(mark, 2);
            NotifyStrip.Children.Add(mark);
        }
    }

    // The sounds whose notify time playback just went through, in [from, to) of the cycle
    private void FireNotifySounds(float fromPhase, float toPhase, bool wrapped)
    {
        if (SoundsToggle.IsChecked != true || AnimationSource is not { } source || _pose is null || _notifies.Length == 0) return;
        var duration = _pose.Duration;
        float from = fromPhase * duration, to = toPhase * duration;
        foreach (var notify in _notifies)
        {
            if (notify.Kind != NotifyKind.Sound || notify.AssetPath is null) continue;
            var crossed = wrapped ? notify.Time >= from || notify.Time < to : notify.Time >= from && notify.Time < to;
            if (!crossed) continue;
            var sound = source.DecodeNotifySoundAsync(notify.AssetPath);
            if (sound.IsCompletedSuccessfully && sound.Result is { } pcm) _notifySounds.Play(pcm);
        }
    }

    // Effects (and sounds) active at the current time, at their socket on the animated mesh
    private void UpdateEffectMarkers()
    {
        if (_pose is null || _animated is not { } obj || _notifies.Length == 0 || EffectsToggle.IsChecked != true)
        {
            Viewport.EffectMarkers = [];
            return;
        }
        var time = _phase * _pose.Duration;
        var markers = new List<EffectMarker>();
        foreach (var notify in _notifies)
        {
            if (notify.Kind == NotifyKind.Other || time < notify.Time || time > notify.Time + Math.Max(notify.Duration, MarkerSeconds)) continue;
            var (bone, local) = (notify.Socket is { } socket ? obj.Model.FindAttachPoint(socket) : null) ?? (0, Matrix4x4.Identity);
            var label = notify.Kind == NotifyKind.Sound ? $"♪ {notify.Asset}" : notify.Asset ?? notify.Name;
            if (markers.Any(m => m.Label == label && m.Bone == bone)) continue;
            markers.Add(new EffectMarker(obj, bone, local, label, notify.Kind == NotifyKind.Sound ? SoundColor : EffectColor));
        }
        Viewport.EffectMarkers = markers;
    }

    // Labels next to the markers; markers at the same spot stack their labels
    private void PlaceMarkerLabels(IReadOnlyList<(EffectMarker Marker, Point? Position)> markers)
    {
        var visible = markers.Where(m => m.Position is not null).ToArray();
        while (MarkerLabels.Children.Count < visible.Length)
            MarkerLabels.Children.Add(new Border
            {
                Background = LabelBackground, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1),
                Child = new TextBlock { FontSize = 11 }
            });

        var placed = new List<Point>();
        for (var i = 0; i < MarkerLabels.Children.Count; i++)
        {
            var label = (Border) MarkerLabels.Children[i];
            label.IsVisible = i < visible.Length;
            if (i >= visible.Length) continue;
            var (marker, position) = visible[i];
            var point = position!.Value + new Point(10, -9);
            while (placed.Any(p => Math.Abs(p.X - point.X) < 40 && Math.Abs(p.Y - point.Y) < 16)) point += new Point(0, 17);
            placed.Add(point);
            ((TextBlock) label.Child!).Text = marker.Label;
            ((TextBlock) label.Child!).Foreground = new SolidColorBrush(Color.FromRgb(
                (byte) (marker.Color.X * 255), (byte) (marker.Color.Y * 255), (byte) (marker.Color.Z * 255)));
            Canvas.SetLeft(label, point.X);
            Canvas.SetTop(label, point.Y);
        }
        MarkerLabelPositions = markers.Select(m => (m.Marker.Label, m.Position)).ToArray();
    }
}
