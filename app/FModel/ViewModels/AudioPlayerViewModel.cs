using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FModel.Services;
using FModel.Services.Audio;

namespace FModel.ViewModels;

/// <summary>One entry of the playlist.</summary>
public sealed partial class AudioTrack(ExtractedSound sound) : ObservableObject
{
    public ExtractedSound Sound { get; } = sound;
    public string Name => Sound.Name;
    public string Details => $"{Sound.Extension.ToUpperInvariant()} · {AssetPreview.FormatSize(Sound.Data.Length)}";
    public string ToolTip => $"{Sound.Path}.{Sound.Extension}";

    /// <summary>Decoded samples, once played.</summary>
    public PcmAudio? Pcm { get; set; }

    [ObservableProperty] private bool _isCurrent;
}

/// <summary>FModel's Audio Player: a playlist fed by extracted sounds, played through the system audio output.</summary>
public sealed partial class AudioPlayerViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly AudioOutput _output = new();
    private int _decodeVersion;

    public ObservableCollection<AudioTrack> Tracks { get; } = [];
    public ObservableCollection<AudioTrack> VisibleTracks { get; } = [];
    public ObservableCollection<AudioDevice> Devices { get; } = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private AudioTrack? _selectedTrack;
    [ObservableProperty] private AudioTrack? _current;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private AudioDevice? _selectedDevice;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private string _encoding = "-";
    [ObservableProperty] private string _length = "-";
    [ObservableProperty] private string _bytesPerSecond = "-";
    [ObservableProperty] private string? _status;

    /// <summary>Raised on the UI thread when something should be logged in the main window.</summary>
    public event Action<LogKind, string>? Message;

    public AudioPlayerViewModel(AppSettings settings)
    {
        _settings = settings;
        _volume = Math.Clamp(settings.AudioVolume, 0, 100);
        _output.Volume = (float) (_volume / 100);
        _output.Ended += () => Dispatcher.UIThread.Post(() => IsPlaying = false);
        RefreshDevices();
        Tracks.CollectionChanged += (_, _) => ApplyFilter();
    }

    public AppSettings Settings => _settings;
    public PcmAudio? Audio => _output.Audio;
    public TimeSpan Position => _output.Position;

    public void RefreshDevices()
    {
        var current = SelectedDevice?.Uid ?? _settings.AudioDevice;
        Devices.Clear();
        Devices.Add(new AudioDevice("", "Default output"));
        try
        {
            foreach (var device in AudioOutput.OutputDevices()) Devices.Add(device);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not list the audio devices");
        }
        SelectedDevice = Devices.FirstOrDefault(d => d.Uid == current) ?? Devices[0];
    }

    /// <summary>Adds sounds to the playlist; with <paramref name="autoplay"/>, plays the first one unless something is playing.</summary>
    public void Add(IReadOnlyList<ExtractedSound> sounds, bool autoplay)
    {
        AudioTrack? first = null;
        foreach (var sound in sounds)
        {
            // Extracting the same sound again selects it instead of duplicating it
            var track = Tracks.FirstOrDefault(t => t.Sound.Path == sound.Path && t.Sound.Extension == sound.Extension);
            if (track is null) Tracks.Add(track = new AudioTrack(sound));
            first ??= track;
        }
        if (first is null) return;
        SelectedTrack = first;
        if (autoplay && !IsPlaying) _ = PlayAsync(first);
    }

    public async Task PlayAsync(AudioTrack track)
    {
        var version = ++_decodeVersion;
        Current = track;
        foreach (var t in Tracks) t.IsCurrent = t == track;
        _output.Stop();
        IsPlaying = false;

        if (track.Pcm is null)
        {
            IsLoading = true;
            Status = $"Decoding {track.Name}…";
            try
            {
                var key = _settings.CriwareKeyValue;
                track.Pcm = await Task.Run(() => AudioDecoder.Decode(track.Sound.Data, track.Sound.Extension, key));
            }
            catch (Exception e)
            {
                if (version != _decodeVersion) return;
                IsLoading = false;
                Status = e.Message;
                Encoding = Length = BytesPerSecond = "-";
                Message?.Invoke(e is MissingDecoderException ? LogKind.Warning : LogKind.Error, $"{track.Name}: {e.Message}");
                if (e is not MissingDecoderException) Serilog.Log.Error(e, "Could not decode {Sound}", track.ToolTip);
                return;
            }
            if (version != _decodeVersion) return; // another track was picked meanwhile
            IsLoading = false;
        }

        var pcm = track.Pcm;
        Encoding = $"{pcm.Encoding} · {pcm.Channels} ch · {pcm.SampleRate:N0} Hz";
        Length = FormatTime(pcm.Duration);
        BytesPerSecond = pcm.Duration.TotalSeconds > 0 ? $"{track.Sound.Data.Length / pcm.Duration.TotalSeconds:N0}" : "-";
        Status = null;
        try
        {
            _output.DeviceUid = string.IsNullOrEmpty(SelectedDevice?.Uid) ? null : SelectedDevice.Uid;
            _output.Load(pcm);
            _output.Play();
            IsPlaying = _output.IsPlaying;
        }
        catch (Exception e)
        {
            Status = e.Message;
            Serilog.Log.Error(e, "Could not play {Sound}", track.ToolTip);
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (IsLoading) return;
        if (Current is null || _output.Audio is null)
        {
            if ((SelectedTrack ?? VisibleTracks.FirstOrDefault()) is { } track) await PlayAsync(track);
            return;
        }
        if (IsPlaying)
        {
            _output.Pause();
            IsPlaying = false;
        }
        else
        {
            _output.Play();
            IsPlaying = _output.IsPlaying;
        }
    }

    [RelayCommand]
    private Task Next() => Step(1);

    [RelayCommand]
    private Task Previous() => Step(-1);

    private Task Step(int direction)
    {
        if (VisibleTracks.Count == 0) return Task.CompletedTask;
        var index = Current is null ? -1 : VisibleTracks.IndexOf(Current);
        var next = VisibleTracks[((index + direction) % VisibleTracks.Count + VisibleTracks.Count) % VisibleTracks.Count];
        SelectedTrack = next;
        return PlayAsync(next);
    }

    [RelayCommand]
    private Task PlaySelected() => SelectedTrack is { } track ? PlayAsync(track) : Task.CompletedTask;

    [RelayCommand]
    private void Remove()
    {
        if (SelectedTrack is not { } track) return;
        var index = VisibleTracks.IndexOf(track);
        if (track == Current)
        {
            _output.Close();
            IsPlaying = false;
            Current = null;
            Encoding = Length = BytesPerSecond = "-";
        }
        Tracks.Remove(track);
        SelectedTrack = VisibleTracks.Count == 0 ? null : VisibleTracks[Math.Clamp(index, 0, VisibleTracks.Count - 1)];
    }

    [RelayCommand]
    private Task Save() => SelectedTrack is { } track ? SaveAsync([track]) : Task.CompletedTask;

    [RelayCommand]
    private Task SavePlaylist() => SaveAsync(VisibleTracks.ToArray());

    private async Task SaveAsync(IReadOnlyList<AudioTrack> tracks)
    {
        var saved = 0;
        foreach (var track in tracks)
        {
            try
            {
                var path = await Task.Run(() => AudioExport.Save(track.Sound, _settings, track.Pcm));
                saved++;
                LastSavedPath = path;
                Message?.Invoke(LogKind.Information, $"Successfully saved '{Path.GetFileName(path)}'");
            }
            catch (Exception e)
            {
                Message?.Invoke(LogKind.Error, $"Could not save '{track.Name}': {e.Message}");
            }
        }
        Status = tracks.Count > 1 ? $"Saved {saved}/{tracks.Count} sounds" : null;
    }

    public string? LastSavedPath { get; private set; }

    /// <summary>Jumps to a point of the current sound (0..1).</summary>
    public void Seek(double fraction)
    {
        if (_output.Audio is not { } audio) return;
        _output.Seek(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * audio.Duration.TotalSeconds));
        IsPlaying = _output.IsPlaying;
    }

    public void Pause()
    {
        _output.Pause();
        IsPlaying = false;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnVolumeChanged(double value)
    {
        _output.Volume = (float) (value / 100);
        _settings.AudioVolume = value;
    }

    partial void OnSelectedDeviceChanged(AudioDevice? value)
    {
        if (value is null) return;
        _settings.AudioDevice = string.IsNullOrEmpty(value.Uid) ? null : value.Uid;
        try
        {
            _output.DeviceUid = _settings.AudioDevice;
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }

    private void ApplyFilter()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var visible = Tracks.Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (visible.SequenceEqual(VisibleTracks)) return;
        VisibleTracks.Clear();
        foreach (var track in visible) VisibleTracks.Add(track);
    }

    public static string FormatTime(TimeSpan time) => $"{(int) time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";

    public void Dispose() => _output.Dispose();
}
