using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FModelMac.Rendering;

namespace FModelMac.Views;

// FModel opens meshes in "Snooper", its own OpenGL window; this is the macOS counterpart.
// The scene holds several meshes side by side, or one map; animations play on the selected skeletal mesh.
public partial class ModelViewerWindow : Window
{
    private static readonly float[] Speeds = [0.25f, 0.5f, 1, 2];

    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _frameWatch = new();
    private IViewerMotion? _motion;
    private AnimationPose? _pose;
    private SceneObject? _animated;  // the object _pose drives
    private float _phase;           // 0..1 through the cycle; seconds = phase × pose duration
    private bool _playing, _updatingSlider;

    public ModelViewerWindow()
    {
        InitializeComponent();
        Viewport.RendererReady += (_, _) => RendererText.Text = Viewport.RendererInfo;
        SceneList.ItemsSource = Scene.Items;
        SceneList.SelectionChanged += (_, _) => ShowSelection(SceneList.SelectedItem as SceneItem);

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render, (_, _) => Tick());
        TimeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_updatingSlider) SeekPhase((float) TimeSlider.Value);
        };
        AxisXSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateBlendWeights(); };
        AxisYSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateBlendWeights(); };
        Closed += (_, _) => _clock.Stop();
        RootMotionToggle.IsCheckedChanged += (_, _) =>
        {
            if (_pose is null) return;
            _pose.RootMotion = RootMotionToggle.IsChecked == true;
            Viewport.RequestNextFrameRendering();
        };
    }

    public ModelViewport ModelViewport => Viewport;
    public ViewerScene Scene => Viewport.Scene;
    public IViewerMotion? Motion => _motion;
    public float AnimationTime => _pose is null ? 0 : _phase * _pose.Duration;

    /// <summary>Adds a mesh next to the others (a map in the scene is replaced) and selects it.</summary>
    public SceneItem AddModel(ViewerModel model)
    {
        if (Scene.Items.Any(i => i.Kind == SceneItem.MapKind)) ClearScene();
        Watch(model);
        var item = Scene.AddBeside(SceneItem.ForModel(model));
        SceneList.SelectedItem = item;
        Viewport.FrameAll();
        return item;
    }

    /// <summary>Replaces the scene with a map (and its hidden sky/backdrop item, when it has one).</summary>
    public void ShowMap(params SceneItem[] items)
    {
        ClearScene();
        foreach (var item in items) Scene.Items.Add(item);
        SceneList.SelectedItem = items[0];
        Viewport.FrameModel();
    }

    public void ClearScene()
    {
        StopAnimation();
        foreach (var item in Scene.Items) Unwatch(item);
        Scene.Items.Clear();
        ShowSelection(null);
    }

    private void Remove(SceneItem item)
    {
        if (_animated is not null && item.Objects.Contains(_animated)) StopAnimation();
        Unwatch(item);
        Scene.Items.Remove(item);
        if (Scene.Items.Count == 0) ShowSelection(null);
    }

    private void Watch(ViewerModel model)
    {
        foreach (var section in model.Sections) section.PropertyChanged += OnModelPartChanged;
        foreach (var morph in model.Morphs) morph.PropertyChanged += OnModelPartChanged;
    }

    private void Unwatch(SceneItem item)
    {
        foreach (var model in item.Objects.Select(o => o.Model))
        {
            foreach (var section in model.Sections) section.PropertyChanged -= OnModelPartChanged;
            foreach (var morph in model.Morphs) morph.PropertyChanged -= OnModelPartChanged;
        }
    }

    // ---------- Selection panel ----------

    private void ShowSelection(SceneItem? item)
    {
        Viewport.Selected = item;
        PlaceholderText.IsVisible = Scene.Items.Count == 0;
        Title = item is null ? "3D Viewer" : $"3D Viewer - {item.Name}";
        NameText.Text = item?.Name ?? "";
        ToolTip.SetTip(NameText, item?.Name);
        KindText.Text = item?.Kind ?? "";

        var objects = item?.Objects ?? [];
        var isMap = item?.Kind == SceneItem.MapKind;
        LodLabel.Text = isMap ? "Meshes" : "LODs";
        LodText.Text = isMap ? objects.Length.ToString("N0") : item?.Single?.Model.LodCount.ToString("N0") ?? "";
        VerticesText.Text = item is null ? "" : objects.Sum(o => (long) o.Model.VertexCount * o.Instances.Length).ToString("N0");
        TrianglesText.Text = item is null ? "" : objects.Sum(o => (long) o.Model.TriangleCount * o.Instances.Length).ToString("N0");
        // UE units are centimeters
        var size = item is null ? default : (item.Max - item.Min) / 100;
        SizeText.Text = item is null ? "" : $"{size.X:0.##} × {size.Z:0.##} × {size.Y:0.##} m";

        var single = item?.Single?.Model;
        MaterialsHeader.IsVisible = SectionsList.IsVisible = single is not null;
        SectionsList.ItemsSource = single?.Sections;

        var hasMorphs = single is { Morphs.Length: > 0 };
        MorphsHeader.IsVisible = hasMorphs;
        MorphsList.ItemsSource = single?.Morphs;
        ListsGrid.RowDefinitions[2].Height = hasMorphs ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
        UpdateMorphsInfo();

        BonesToggle.IsEnabled = single is { IsSkinned: true };
        if (!BonesToggle.IsEnabled) BonesToggle.IsChecked = false;
    }

    // ---------- Animation ----------

    /// <summary>
    /// Plays an animation or blend space on the selected skeletal mesh (or the last one added);
    /// returns an error message when it can't.
    /// </summary>
    public string? PlayAnimation(IViewerMotion motion)
    {
        var target = (SceneList.SelectedItem as SceneItem)?.Single is { Model.IsSkinned: true } selected
            ? selected
            : Scene.Items.Reverse().Select(i => i.Single).FirstOrDefault(o => o is { Model.IsSkinned: true });
        if (target is null)
            return "Open a skeletal mesh in the 3D viewer first, then double-click the animation";

        var pose = new AnimationPose(target.Model, motion);
        if (pose.MappedBones == 0)
            return $"{motion.Name} doesn't share any bone with {target.Model.Name} (skeleton {motion.SkeletonName})";

        if (_animated is not null && _animated != target) _animated.Pose = null; // one animation at a time
        _animated = target;
        _motion = motion;
        _pose = pose;
        pose.RootMotion = RootMotionToggle.IsChecked == true;
        target.Pose = pose;
        TimelineBar.IsVisible = true;
        SetupBlendSpace(motion as ViewerBlendSpace);

        var bones = target.Model.Bones!.Length;
        var mapped = $"{pose.MappedBones}/{bones} bones of {target.Model.Name} mapped from {motion.SkeletonName}";
        var curves = motion.Clips.SelectMany(c => c.Segments).SelectMany(s => s.Curves).Select(c => c.CurveName.Text).Distinct().Count();
        var curveText = curves > 0 ? $" · {curves} curve(s)" : "";
        AnimationText.Text = motion switch
        {
            ViewerBlendSpace bs => $"{bs.Name} · blend space with {bs.Samples.Length} samples on {bs.Axes.Length} " +
                                   $"{(bs.Axes.Length > 1 ? "axes" : "axis")}{curveText} · {mapped}",
            ViewerAnimation a => $"{a.Name} · {a.Duration:0.00} s · {a.FrameCount:N0} frames at {a.FrameRate:0.#} fps · " +
                                 $"{a.Segments.Length} sequence(s){curveText} · {mapped}",
            _ => mapped
        };

        _phase = 0;
        UpdateTime();
        UpdateMorphsInfo();
        SetPlaying(true);
        Viewport.RequestNextFrameRendering();
        return null;
    }

    private void SetupBlendSpace(ViewerBlendSpace? blendSpace)
    {
        var axes = blendSpace?.Axes ?? [];
        Row(AxisXName, AxisXSlider, AxisXValue, axes.Length > 0 ? axes[0] : null);
        Row(AxisYName, AxisYSlider, AxisYValue, axes.Length > 1 ? axes[1] : null);
        BlendWeightsText.IsVisible = blendSpace is not null;
        UpdateBlendWeights();

        static void Row(TextBlock name, Slider slider, TextBlock value, ViewerBlendSpace.Axis? axis)
        {
            name.IsVisible = slider.IsVisible = value.IsVisible = axis is not null;
            if (axis is null) return;
            name.Text = axis.Name;
            slider.Minimum = axis.Min;
            slider.Maximum = axis.Max;
            slider.Value = axis.Min;
        }
    }

    private void UpdateBlendWeights()
    {
        if (_motion is not ViewerBlendSpace blendSpace || _pose is null) return;

        var x = (float) AxisXSlider.Value;
        var y = (float) AxisYSlider.Value;
        AxisXValue.Text = $"{x:0.##}";
        AxisYValue.Text = $"{y:0.##}";
        _pose.Weights = blendSpace.Weights(x, y);
        BlendWeightsText.Text = string.Join("  ·  ", _pose.Weights
            .Select((w, i) => (Weight: w, blendSpace.Samples[i].Name))
            .Where(s => s.Weight > 0.005f)
            .OrderByDescending(s => s.Weight)
            .Select(s => $"{s.Name} {s.Weight:P0}"));
        UpdateTime();
        Viewport.RequestNextFrameRendering();
    }

    /// <summary>Sets the blend space parameters (used by the test harness).</summary>
    public void SetBlendParameters(float x, float y = 0)
    {
        AxisXSlider.Value = x;
        AxisYSlider.Value = y;
        UpdateBlendWeights();
    }

    private void StopAnimation()
    {
        SetPlaying(false);
        if (_animated is not null) _animated.Pose = null;
        _animated = null;
        _motion = null;
        _pose = null;
        _phase = 0;
        TimelineBar.IsVisible = false;
        UpdateMorphsInfo();
        Viewport.RequestNextFrameRendering();
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing && _pose is not null;
        PlayButton.Content = _playing ? "Pause" : "Play";
        if (_playing)
        {
            _frameWatch.Restart();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void Tick()
    {
        if (!_playing || _pose is null) return;

        var elapsed = (float) _frameWatch.Elapsed.TotalSeconds;
        _frameWatch.Restart();
        _phase += elapsed * Speeds[Math.Clamp(SpeedBox.SelectedIndex, 0, Speeds.Length - 1)] / _pose.Duration;
        if (_phase > 1)
        {
            if (LoopToggle.IsChecked == true) _phase %= 1;
            else
            {
                _phase = 1;
                SetPlaying(false);
            }
        }
        UpdateTime();
    }

    /// <summary>Jumps to a time in seconds (used by the test harness).</summary>
    public void Seek(float seconds)
    {
        if (_pose is null) return;
        SeekPhase(seconds / _pose.Duration);
    }

    private void SeekPhase(float phase)
    {
        if (_pose is null) return;
        _phase = Math.Clamp(phase, 0, 1);
        UpdateTime();
    }

    public void Pause() => SetPlaying(false);

    private void UpdateTime()
    {
        Viewport.AnimationPhase = _phase;
        _updatingSlider = true;
        TimeSlider.Value = _phase;
        _updatingSlider = false;
        var duration = _pose?.Duration ?? 0;
        var frame = _motion is ViewerAnimation a ? $"  #{(int) (_phase * duration * a.FrameRate)}" : "";
        TimeText.Text = $"{_phase * duration:0.00} / {duration:0.00} s{frame}";
    }

    private void UpdateMorphsInfo()
    {
        if ((SceneList.SelectedItem as SceneItem)?.Single is not { Model.Morphs.Length: > 0 } obj) return;
        var driven = obj == _animated && _motion is not null
            ? _motion.Clips.SelectMany(c => c.Segments).SelectMany(s => s.Curves).Select(c => c.CurveName.Text).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var count = obj.Model.Morphs.Count(m => driven.Contains(m.Name));
        MorphsInfo.Text = count > 0
            ? $"{obj.Model.Morphs.Length} morphs · {count} driven by the animation's curves"
            : $"{obj.Model.Morphs.Length} morphs · drag to blend";
    }

    // ---------- UI handlers ----------

    private void OnResetMorphsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var morph in (SceneList.SelectedItem as SceneItem)?.Single?.Model.Morphs ?? []) morph.Weight = 0;
    }

    private void OnRemoveItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SceneItem item) Remove(item);
    }

    private void OnClearSceneClick(object? sender, RoutedEventArgs e) => ClearScene();

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (!_playing && _phase >= 1) _phase = 0;
        SetPlaying(!_playing);
    }

    private void OnStopAnimationClick(object? sender, RoutedEventArgs e) => StopAnimation();

    private void OnModelPartChanged(object? sender, PropertyChangedEventArgs e) => Viewport.RequestNextFrameRendering();

    private void OnFrameClick(object? sender, RoutedEventArgs e) => Viewport.FrameModel();

    private async void OnSaveScreenshotClick(object? sender, RoutedEventArgs e)
    {
        var png = await Viewport.CaptureAsync();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Screenshot",
            SuggestedFileName = $"{(SceneList.SelectedItem as SceneItem)?.Name ?? "viewer"}.png",
            DefaultExtension = "png",
            FileTypeChoices = [FilePickerFileTypes.ImagePng]
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
    }
}
