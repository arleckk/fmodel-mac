using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FModelMac.Services;
using FModelMac.Services.Audio;
using FModelMac.ViewModels;

namespace FModelMac.Views;

// Views > Audio Player: FModel's player (playlist, devices, volume, spectrum, timeline)
public partial class AudioPlayerWindow : Window
{
    private readonly AudioPlayerViewModel _vm;
    private readonly DispatcherTimer _timer;

    // Designer only
    public AudioPlayerWindow() : this(new AudioPlayerViewModel(new AppSettings())) { }

    public AudioPlayerWindow(AudioPlayerViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // ~30 fps while something moves; the timeline also follows seeks while paused
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AudioPlayerViewModel.Current) or nameof(AudioPlayerViewModel.IsPlaying)) Tick();
            if (e.PropertyName is nameof(AudioPlayerViewModel.SelectedTrack) && vm.SelectedTrack is { } track)
                Playlist.ScrollIntoView(track);
        };
    }

    public AudioPlayerViewModel ViewModel => _vm;

    private void Tick()
    {
        ClockTime.Text = AudioPlayerViewModel.FormatTime(_vm.Position);
        if (Spectrum.IsAnimating) Spectrum.Update();
        Timeline.Update();
    }

    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.SelectedTrack is { } track) await _vm.PlayAsync(track);
    }

    // FModel's Add Audio File: sounds from disk, next to the extracted ones (banks need their game, so not those)
    private static readonly string[] LocalAudio = ["wem", "wav", "ogg", "flac", "at9", "xvag", "binka", "hca", "adx", "opus", "mp3"];

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e) => await AddFilesAsync();

    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Audio Files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio Files") { Patterns = LocalAudio.Select(x => $"*.{x}").ToArray() }]
        });
        var sounds = new List<ExtractedSound>();
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            sounds.Add(new ExtractedSound(name, name, Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), await File.ReadAllBytesAsync(path)));
        }
        _vm.Add(sounds, autoplay: false);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        // Leave typing in the search box alone
        if (e.Source is TextBox) { base.OnKeyDown(e); return; }

        // Settings › Hotkeys (FModel's audio player keys) first, then the built-in ones
        var hotkeys = _vm.Settings.Hotkeys;
        if (hotkeys.AddAudio.IsTriggered(e))
        {
            e.Handled = true;
            await AddFilesAsync();
            return;
        }
        if (hotkeys.PlayPauseAudio.IsTriggered(e))
        {
            e.Handled = true;
            await _vm.PlayPauseCommand.ExecuteAsync(null);
            return;
        }
        if (hotkeys.PreviousAudio.IsTriggered(e))
        {
            e.Handled = true;
            await _vm.PreviousCommand.ExecuteAsync(null);
            return;
        }
        if (hotkeys.NextAudio.IsTriggered(e))
        {
            e.Handled = true;
            await _vm.NextCommand.ExecuteAsync(null);
            return;
        }
        if (hotkeys.RemoveAudio.IsTriggered(e))
        {
            e.Handled = true;
            _vm.RemoveCommand.Execute(null);
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                await _vm.PlayPauseCommand.ExecuteAsync(null);
                return;
            case Key.Right when e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                e.Handled = true;
                await _vm.NextCommand.ExecuteAsync(null);
                return;
            case Key.Left when e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                e.Handled = true;
                await _vm.PreviousCommand.ExecuteAsync(null);
                return;
            case Key.Delete or Key.Back:
                e.Handled = true;
                _vm.RemoveCommand.Execute(null);
                return;
            case Key.Enter:
                e.Handled = true;
                await _vm.PlaySelectedCommand.ExecuteAsync(null);
                return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _vm.Pause(); // the playlist survives in the main window; reopening continues from here
    }
}
