using Avalonia.Controls;
using Avalonia.Interactivity;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

// Directory › Backup (FModel's BackupManager): create a backup of the current game, compare with one, or download
// one of FModel's
public partial class BackupWindow : Window
{
    private readonly MainViewModel _vm;

    // Designer only
    public BackupWindow() : this(new MainViewModel(), loadOnline: false) { }

    /// <param name="loadOnline">Whether to fetch FModel's list right away, like FModel does when the window opens.</param>
    public BackupWindow(MainViewModel vm, bool loadOnline = true)
    {
        _vm = vm;
        InitializeComponent();
        CreateText.Text = vm.Archive is { } archive
            ? $"{archive.ProjectName}: {GameBackup.FileCount(archive.Provider):N0} files, saved to {GameBackup.Directory(vm.Settings)}"
            : "Mount a game first (Directory › Selector)";
        RefreshLocal();
        DownloadButton.IsEnabled = false;
        if (loadOnline) Opened += async (_, _) => await RefreshOnlineAsync();
    }

    private BackupFile? SelectedLocal => LocalList.SelectedItem as BackupFile;

    private void RefreshLocal(string? select = null)
    {
        var backups = GameBackup.List(_vm.Settings);
        LocalList.ItemsSource = backups;
        LocalList.SelectedItem = backups.FirstOrDefault(b => b.Path == select) ?? backups.FirstOrDefault();
        LocalEmpty.Text = backups.Count == 0 ? "No backups yet in " + GameBackup.Directory(_vm.Settings) : "";
        OnLocalSelectionChanged(null, null);
    }

    private void OnLocalSelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        var selected = SelectedLocal is not null;
        RevealButton.IsEnabled = selected;
        LoadNewButton.IsEnabled = LoadModifiedButton.IsEnabled = selected && _vm.Archive is not null;
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (await _vm.CreateBackupAsync() is { } path) RefreshLocal(path);
    }

    private void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedLocal is { } backup) AppSettings.RevealInFinder(backup.Path);
    }

    private async void OnLoadNewClick(object? sender, RoutedEventArgs e) => await LoadChangesAsync(true);

    private async void OnLoadModifiedClick(object? sender, RoutedEventArgs e) => await LoadChangesAsync(false);

    private async Task LoadChangesAsync(bool newFiles)
    {
        if (SelectedLocal is not { } backup) return;
        _vm.LoadingMode = newFiles ? MainViewModel.LoadNew : MainViewModel.LoadModified;
        if (await _vm.LoadBackupChangesAsync(backup.Path, newFiles) is not null) Close();
    }

    private async void OnRefreshOnlineClick(object? sender, RoutedEventArgs e) => await RefreshOnlineAsync();

    private async Task RefreshOnlineAsync()
    {
        OnlineText.Text = "Loading FModel's backups…";
        try
        {
            var backups = await OnlineBackups.ListAsync();
            OnlineBox.ItemsSource = backups.Select(b => b.Display).ToArray();
            OnlineBox.Tag = backups;
            OnlineBox.SelectedIndex = backups.Count > 0 ? 0 : -1;
            DownloadButton.IsEnabled = backups.Count > 0;
            OnlineText.Text = backups.Count > 0 ? $"{backups.Count} backup(s) available" : "FModel's service has no backups right now";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not list FModel's backups");
            OnlineText.Text = $"Could not reach FModel's service: {ex.Message}";
        }
    }

    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (OnlineBox.Tag is not IReadOnlyList<OnlineBackup> backups || OnlineBox.SelectedIndex < 0) return;
        var backup = backups[OnlineBox.SelectedIndex];
        DownloadButton.IsEnabled = false;
        OnlineText.Text = $"Downloading {backup.Display}…";
        try
        {
            var path = await OnlineBackups.DownloadAsync(backup, GameBackup.Directory(_vm.Settings));
            _vm.Log(LogKind.Information, $"Successfully downloaded '{backup.FileName}'");
            OnlineText.Text = $"Downloaded {backup.FileName}";
            RefreshLocal(path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not download {Backup}", backup.FileName);
            _vm.Log(LogKind.Error, $"Could not download '{backup.FileName}': {ex.Message}");
            OnlineText.Text = $"Could not download it: {ex.Message}";
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
