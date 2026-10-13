using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FModel.Rendering;
using FModel.Services.Video;

namespace FModel.Views;

// "Save Video…": one cycle of the playing animation, rendered offline frame by frame from the current camera (so no
// frame is dropped, whatever the scene costs), with its effects and its notify sounds mixed into the soundtrack
public partial class ModelViewerWindow
{
    private const int VideoFps = 60;
    private const int VideoMaxWidth = 1920, VideoMaxHeight = 1080;

    private CancellationTokenSource? _recording;

    /// <summary>Whether a video is being rendered (test harness).</summary>
    public bool IsRecording => _recording is not null;

    private async void OnSaveVideoClick(object? sender, RoutedEventArgs e)
    {
        if (_recording is { } running)
        {
            running.Cancel();
            return;
        }
        if (_pose is null) return;

        var name = _playingEntry?.Name ?? (SceneList.SelectedItem as SceneItem)?.Name ?? "animation";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Video",
            SuggestedFileName = $"{name}.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices =
            [
                new FilePickerFileType("MPEG-4 video") { Patterns = ["*.mp4"], AppleUniformTypeIdentifiers = ["public.mpeg-4"] },
                new FilePickerFileType("QuickTime movie") { Patterns = ["*.mov"], AppleUniformTypeIdentifiers = ["com.apple.quicktime-movie"] }
            ]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await RecordVideoAsync(path);
    }

    /// <summary>The video's size: the viewport's shape, fitted in 1920 × 1080, even sides (H.264 needs them).</summary>
    private (int Width, int Height) VideoSize()
    {
        double width = Math.Max(16, Viewport.Bounds.Width), height = Math.Max(16, Viewport.Bounds.Height);
        var scale = Math.Min(VideoMaxWidth / width, VideoMaxHeight / height);
        return ((int) (width * scale) & ~1, (int) (height * scale) & ~1);
    }

    /// <summary>
    /// Renders one cycle of the animation (at the chosen speed; a last frame on the end pose when it doesn't loop) to
    /// <paramref name="path"/>. Returns null when saved, else why not ("cancelled" when stopped).
    /// </summary>
    public async Task<string?> RecordVideoAsync(string path)
    {
        if (_pose is not { } pose || _recording is not null) return "no animation";
        var speed = Speeds[Math.Clamp(SpeedBox.SelectedIndex, 0, Speeds.Length - 1)];
        var loop = LoopToggle.IsChecked == true;
        var seconds = pose.Duration / speed;
        var frames = Math.Max(1, (int) Math.Round(seconds * VideoFps)) + (loop ? 0 : 1);
        var (width, height) = VideoSize();

        var wasPlaying = _playing;
        var phase = _phase;
        SetPlaying(false);
        _notifySounds.StopAll();
        var recording = _recording = new CancellationTokenSource();
        TimelineBar.IsEnabled = false;
        SaveVideoButton.Content = "Cancel Video";
        var info = AnimationText.Text;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        VideoWriter? writer = null;
        try
        {
            // Effects drawn in the frames must be read first; the soundtrack is mixed up front
            if (AnimationSource is { } source)
                foreach (var effect in _notifies.Where(n => n.Kind == NotifyKind.Effect).Select(n => n.AssetPath).OfType<string>().Distinct())
                    await source.LoadEffectAsync(effect);
            var audio = SoundsToggle.IsChecked == true ? await MixNotifySoundsAsync((double) frames / VideoFps, speed) : null;
            writer = await Task.Run(() => new VideoWriter(path, width, height, VideoFps, audio));

            // Encoding a frame overlaps rendering the next one
            var encoding = Task.CompletedTask;
            for (var i = 0; i < frames; i++)
            {
                recording.Token.ThrowIfCancellationRequested();
                if (_pose != pose) throw new OperationCanceledException();
                SeekPhase(Math.Min(1, i / (float) VideoFps * speed / pose.Duration));
                var pixels = await Viewport.CaptureVideoFrameAsync(width, height);
                await encoding;
                var frameWriter = writer;
                encoding = Task.Run(() => frameWriter.AppendFrame(pixels));
                SaveVideoButton.Content = $"Cancel Video ({(i + 1) * 100 / frames}%)";
            }
            await encoding;
            await Task.Run(writer.Finish);
            Serilog.Log.Information("Saved a {Width}×{Height} video of {Frames} frames to {Path} in {Seconds:F1} s",
                width, height, frames, path, watch.Elapsed.TotalSeconds);
            info = $"{info} · saved {Path.GetFileName(path)} ({width}×{height}, {VideoFps} fps, {frames / (double) VideoFps:0.0} s" +
                   $"{(audio is null ? "" : ", with sound")})";
            return null;
        }
        catch (OperationCanceledException)
        {
            writer?.Cancel();
            return "cancelled";
        }
        catch (Exception e)
        {
            writer?.Cancel();
            Serilog.Log.Error(e, "Could not save the video {Path}", path);
            info = $"{info} · video not saved: {e.Message}";
            return e.Message;
        }
        finally
        {
            _recording = null;
            recording.Dispose();
            TimelineBar.IsEnabled = true;
            SaveVideoButton.Content = "Save Video…";
            AnimationText.Text = info;
            if (_pose == pose)
            {
                SeekPhase(phase);
                SetPlaying(wasPlaying);
            }
        }
    }

    // The notify sounds where they play in the video (at normal pitch, whatever the speed), as 48 kHz stereo
    private async Task<float[]?> MixNotifySoundsAsync(double seconds, float speed)
    {
        if (AnimationSource is not { } source) return null;
        var total = (int) (seconds * VideoWriter.AudioRate);
        float[]? mix = null;
        foreach (var notify in _notifies)
        {
            if (notify.Kind != NotifyKind.Sound || notify.AssetPath is null) continue;
            if (await source.DecodeNotifySoundAsync(notify.AssetPath) is not { Samples.Length: > 0 } pcm) continue;
            mix ??= new float[total * 2];

            var start = (long) (notify.Time / speed * VideoWriter.AudioRate);
            var step = (double) pcm.SampleRate / VideoWriter.AudioRate;
            var channels = Math.Max(1, pcm.Channels);
            var length = pcm.Samples.Length / channels;
            var volume = _notifySounds.Volume;
            for (var j = Math.Max(0, start); j < total; j++)
            {
                var position = (j - start) * step;
                var index = (int) position;
                if (index + 1 >= length) break;
                var fraction = (float) (position - index);
                float Sample(int channel) =>
                    pcm.Samples[index * channels + channel] * (1 - fraction) + pcm.Samples[(index + 1) * channels + channel] * fraction;
                var left = Sample(0);
                var right = channels > 1 ? Sample(1) : left;
                mix[j * 2] += left * volume;
                mix[j * 2 + 1] += right * volume;
            }
        }
        if (mix is null) return null;
        // Sounds that overlap can add past full scale: a soft limit instead of clipping
        for (var i = 0; i < mix.Length; i++)
            if (Math.Abs(mix[i]) > 0.9f) mix[i] = MathF.Sign(mix[i]) * (0.9f + 0.1f * MathF.Tanh((Math.Abs(mix[i]) - 0.9f) / 0.1f));
        return mix;
    }
}
