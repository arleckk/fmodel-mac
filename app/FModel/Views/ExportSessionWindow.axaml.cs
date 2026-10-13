using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

// Views › Export Session (FModel's ExportSessionWindow)
public partial class ExportSessionWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ExportSessionViewModel _session;
    private readonly DispatcherTimer _clock;

    // Designer only
    public ExportSessionWindow() : this(new MainViewModel()) { }

    public ExportSessionWindow(MainViewModel vm)
    {
        _vm = vm;
        _session = vm.ExportSession;
        DataContext = _session;
        InitializeComponent();

        OptionsPanel.Values = _session.OverrideValues ?? ExportOptionValues.FromSettings(vm.Settings);
        OutputBox.Text = _session.OverrideDirectory ?? vm.Settings.ExportsDir;

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => _session.UpdateClock());
        _session.PropertyChanged += OnSessionChanged;
        _session.Groups.CollectionChanged += (_, _) => UpdateStatus();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _session.PropertyChanged -= OnSessionChanged;
            KeepOverrides();
        };
        UpdateStatus();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportSessionViewModel.IsRunning))
        {
            if (_session.IsRunning) _clock.Start();
            else _clock.Stop();
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = _session switch
        {
            { IsRunning: true } => $"Exporting… {_session.Progress:P0}",
            { IsCanceled: true } => "Export canceled",
            { IsFinished: true } => _session.FailedCount > 0
                ? $"Finished with {_session.FailedCount} failed asset(s)"
                : $"Finished: {_session.SucceededCount} asset(s) exported",
            { QueuedCount: 0 } => "Queue is empty",
            _ => $"{_session.QueuedCount} asset(s) queued"
        };
        EmptyText.IsVisible = _session.Groups.Count == 0;
    }

    // The session's own options only matter while "Override" is checked
    private void KeepOverrides()
    {
        _session.OverrideValues = OptionsPanel.Values;
        _session.OverrideDirectory = string.IsNullOrWhiteSpace(OutputBox.Text) ? null : OutputBox.Text.Trim();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        KeepOverrides();
        Tabs.SelectedIndex = 0;
        await _vm.RunExportSessionAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _session.Cancel();

    private void OnClearClick(object? sender, RoutedEventArgs e) => _session.Clear();

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ExportItem item) _session.Remove(item);
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ExportItem { FirstFile: { } file }) AppSettings.RevealInFinder(file);
    }

    private void OnMakeDefaultClick(object? sender, RoutedEventArgs e)
    {
        OptionsPanel.Values.ApplyTo(_vm.Settings);
        _vm.Settings.Save();
        _session.OverrideOptions = false;
        _vm.Log(LogKind.Information, "Saved the Export Session options as the defaults");
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        OptionsPanel.Values = ExportOptionValues.FromSettings(_vm.Settings);
        OutputBox.Text = _vm.Settings.ExportsDir;
        _session.OverrideOptions = false;
    }

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output Directory" });
        if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) OutputBox.Text = path;
    }
}
