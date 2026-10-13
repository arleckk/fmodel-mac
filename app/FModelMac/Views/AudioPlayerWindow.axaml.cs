using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FModelMac.Services;
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

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        // Leave typing in the search box alone
        if (e.Source is TextBox) { base.OnKeyDown(e); return; }
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
