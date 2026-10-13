using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FModel.Services.Audio;
using FModel.ViewModels;

namespace FModel.Controls;

/// <summary>FModel's SpectrumAnalyzer: frequency bars of what is playing right now.</summary>
public sealed class AudioSpectrum : Control
{
    public static readonly StyledProperty<AudioPlayerViewModel?> PlayerProperty =
        AvaloniaProperty.Register<AudioSpectrum, AudioPlayerViewModel?>(nameof(Player));

    public AudioPlayerViewModel? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private const int BarCount = 75;
    private const int FftSize = 4096;
    private readonly float[] _bars = new float[BarCount];
    private readonly Complex[] _fft = new Complex[FftSize];

    private static readonly IBrush BarBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#B5AC49"), 0), new GradientStop(Color.Parse("#3CA55C"), 1) }
    };
    private static readonly IPen BarPen = new Pen(new SolidColorBrush(Color.Parse("#DAE5F2")), 1);

    /// <summary>True while bars are still moving (playing, or falling back after a pause).</summary>
    public bool IsAnimating => Player?.IsPlaying == true || _bars.Any(b => b > 0.001f);

    public void Update()
    {
        var audio = Player?.Audio;
        Span<float> target = stackalloc float[BarCount];
        if (Player is { IsPlaying: true } && audio is not null) Analyze(audio, Player.Position, target);

        // Rise quickly, fall slowly, like a VU meter
        for (var i = 0; i < BarCount; i++)
            _bars[i] = target[i] > _bars[i] ? target[i] : Math.Max(target[i], _bars[i] - 0.025f);
        InvalidateVisual();
    }

    private void Analyze(PcmAudio audio, TimeSpan position, Span<float> bars)
    {
        var start = (long) (position.TotalSeconds * audio.SampleRate);
        var channels = audio.Channels;
        for (var i = 0; i < FftSize; i++)
        {
            var frame = start + i;
            float sample = 0;
            if (frame < audio.Frames)
                for (var c = 0; c < channels; c++) sample += audio.Samples[frame * channels + c];
            var window = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1)); // Hann
            _fft[i] = new Complex(sample / channels * window, 0);
        }
        Fft(_fft);

        // Logarithmic bands from 30 Hz to the Nyquist frequency (16 kHz at most)
        // (the lowest bands are narrower than a bin: each bar gets at least a bin of its own)
        var nyquist = audio.SampleRate / 2.0;
        double low = 30, high = Math.Min(16000, nyquist);
        var previous = 1;
        for (var b = 0; b < BarCount; b++)
        {
            var f1 = low * Math.Pow(high / low, (double) (b + 1) / BarCount);
            var i0 = Math.Clamp(previous, 1, FftSize / 2 - 1);
            var i1 = Math.Clamp((int) Math.Ceiling(f1 / nyquist * FftSize / 2), i0 + 1, FftSize / 2);
            previous = i1;
            double peak = 0;
            for (var i = i0; i < i1; i++) peak = Math.Max(peak, _fft[i].Magnitude);
            var db = 20 * Math.Log10(peak / (FftSize / 4.0) + 1e-9);
            bars[b] = (float) Math.Clamp((db + 70) / 70, 0, 1);
        }
    }

    private static void Fft(Complex[] data)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[i + k];
                    var odd = data[i + k + length / 2] * w;
                    data[i + k] = even + odd;
                    data[i + k + length / 2] = even - odd;
                    w *= step;
                }
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var slot = width / BarCount;
        var barWidth = Math.Max(1, slot * 0.7);
        for (var i = 0; i < BarCount; i++)
        {
            var h = Math.Round(_bars[i] * height * 0.9);
            if (h < 1) continue;
            var rect = new Rect(Math.Round(i * slot + (slot - barWidth) / 2), height - h, Math.Round(barWidth), h);
            context.DrawRectangle(BarBrush, BarPen, rect);
        }
    }
}

/// <summary>FModel's Timeline: time ruler and playback progress; click or drag to seek. Shows the waveform behind.</summary>
public sealed class AudioTimeline : Control
{
    public static readonly StyledProperty<AudioPlayerViewModel?> PlayerProperty =
        AvaloniaProperty.Register<AudioTimeline, AudioPlayerViewModel?>(nameof(Player));

    public AudioPlayerViewModel? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private const double RulerHeight = 20;
    private static readonly IBrush ProgressBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#45B649"), 0), new GradientStop(Color.Parse("#DCE35B"), 1) }
    };
    private static readonly IBrush LineBrush = Brushes.Brown;
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#7F848E"));
    private static readonly IBrush TimeBrush = new SolidColorBrush(Color.Parse("#DAE5F2"));
    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.Parse("#DAE5F2"), 0.18);
    private static readonly Typeface Font = new("Menlo");
    private static readonly ConditionalWeakTable<PcmAudio, float[]> Peaks = new();

    private double? _hover;
    private bool _dragging;

    public AudioTimeline()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    public void Update() => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SeekTo(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this).X;
        if (_dragging) SeekTo(_hover.Value);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private void SeekTo(double x)
    {
        if (Bounds.Width > 0) Player?.Seek(x / Bounds.Width);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable everywhere
        if (Player?.Audio is not { } audio || width <= 0) return;
        var duration = audio.Duration.TotalSeconds;
        if (duration <= 0) return;

        // Ruler: a tick every "nice" interval that leaves room for its label
        var interval = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.FirstOrDefault(i => i / duration * width >= 80, 1200);
        for (var t = 0.0; t <= duration; t += interval)
        {
            var x = Math.Round(t / duration * width) + 0.5;
            context.DrawLine(new Pen(TickBrush), new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            var label = new FormattedText(Label(t, interval), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, TimeBrush);
            context.DrawText(label, new Point(Math.Min(x + 3, width - label.Width - 2), 2));
        }
        context.DrawLine(new Pen(TickBrush), new Point(0, RulerHeight), new Point(width, RulerHeight));

        // Waveform behind the progress
        var body = new Rect(0, RulerHeight, width, Math.Max(0, height - RulerHeight));
        var peaks = Peaks.GetValue(audio, ComputePeaks);
        var middle = body.Y + body.Height / 2;
        var columns = (int) width;
        for (var x = 0; x < columns; x++)
        {
            var peak = peaks[Math.Min(peaks.Length - 1, x * peaks.Length / columns)];
            var h = Math.Max(1, peak * body.Height / 2);
            context.FillRectangle(WaveBrush, new Rect(x, middle - h, 1, h * 2));
        }

        var progress = Math.Clamp(Player.Position.TotalSeconds / duration, 0, 1) * width;
        if (progress > 0)
        {
            context.FillRectangle(ProgressBrush, new Rect(0, body.Y, progress, body.Height), 0);
            context.FillRectangle(LineBrush, new Rect(Math.Max(0, progress - 1), body.Y, 1, body.Height));
        }

        if (_hover is { } hover && hover >= 0 && hover <= width)
        {
            context.FillRectangle(LineBrush, new Rect(hover - 1, 0, 2, height));
            var at = new FormattedText(AudioPlayerViewModel.FormatTime(TimeSpan.FromSeconds(hover / width * duration)),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, TimeBrush);
            var x = hover + 4 + at.Width > width ? hover - 4 - at.Width : hover + 4;
            context.FillRectangle(new SolidColorBrush(Color.Parse("#2A2B34"), 0.85), new Rect(x - 2, body.Y + 2, at.Width + 4, at.Height));
            context.DrawText(at, new Point(x, body.Y + 2));
        }
    }

    private static string Label(double seconds, double interval)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return interval < 1 ? $"{(int) time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}" : $"{(int) time.TotalMinutes}:{time.Seconds:00}";
    }

    private static float[] ComputePeaks(PcmAudio audio)
    {
        const int buckets = 2048;
        var peaks = new float[buckets];
        var frames = audio.Frames;
        if (frames == 0) return peaks;
        for (var b = 0; b < buckets; b++)
        {
            long from = (long) b * frames / buckets, to = Math.Max(from + 1, (long) (b + 1) * frames / buckets);
            float peak = 0;
            for (var f = from; f < to && f < frames; f++)
            for (var c = 0; c < audio.Channels; c++)
                peak = Math.Max(peak, Math.Abs(audio.Samples[f * audio.Channels + c]));
            peaks[b] = Math.Min(1, peak);
        }
        return peaks;
    }
}

/// <summary>Highlights the playlist entry that is loaded in the player.</summary>
public static class AudioVisualConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> CurrentOpacity = new(current => current ? 1 : 0.35);
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, FontWeight> CurrentWeight = new(current => current ? FontWeight.SemiBold : FontWeight.Normal);
}
