using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

/// <summary>One release in the history: its notes as plain text and whether it's the latest or the installed one.</summary>
public sealed partial class ReleaseItem(ReleaseInfo release, bool isLatest)
{
    public ReleaseInfo Release { get; } = release;
    public bool IsLatest { get; } = isLatest;
    public bool IsInstalled => Release.Version == UpdateChecker.Current;
    public string DateText => Release.Published == DateTimeOffset.MinValue ? "" : Release.Published.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    public bool CanDownload => Release.Download is not null;
    public string DownloadText => Release.Download is { } asset ? $"Download ({AssetPreview.FormatSize(asset.Size)})" : "";
    public string NotesText { get; } = PlainText(release.Notes);

    // The notes are Markdown: headings, emphasis, code and link targets dropped for reading in a TextBlock
    private static string PlainText(string markdown)
    {
        var text = HeadingRegex().Replace(markdown, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = text.Replace("**", "").Replace("`", "");
        return text.Length > 0 ? text : "No release notes.";
    }

    [GeneratedRegex(@"^#{1,6}\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]+\)")]
    private static partial Regex LinkRegex();
}

public partial class ReleasesWindow : Window
{
    private readonly MainViewModel? _vm;

    // Designer only
    public ReleasesWindow() => InitializeComponent();

    /// <param name="updateFound">Opened by the launch check (FModel's "A new update is available!").</param>
    public ReleasesWindow(MainViewModel vm, bool updateFound = false)
    {
        _vm = vm;
        InitializeComponent();
        if (updateFound) Title = "A new update is available!";
        IntroText.Text = $"Every release of FModel for macOS, the latest at the top. You have {UpdateChecker.Current} installed. " +
                         "Download Latest Release downloads the app in your browser: unzip it and replace FModel in Applications " +
                         "(quit it first). This window is also in Help › Releases.";
        NextRefreshText.Text = UpdateChecker.NextRefreshText(vm.Settings.NextUpdateCheck);
        Show(vm.Releases);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm is null || _vm.Releases.Count > 0) return;
        StatusText.Text = "Looking for releases…";
        try
        {
            Show(await _vm.RefreshReleasesAsync());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not list the releases");
            StatusText.Text = $"There is a problem reaching GitHub, please check your internet connection or try again later.\n({ex.Message})";
        }
    }

    private void Show(IReadOnlyList<ReleaseInfo> releases)
    {
        ReleaseList.ItemsSource = releases.Select((r, i) => new ReleaseItem(r, i == 0)).ToArray();
        StatusText.Text = releases.Count == 0 && _vm?.Settings.LastUpdateCheck > DateTime.MinValue ? "No releases found" : "";
        DownloadLatestButton.IsEnabled = releases.FirstOrDefault()?.Download is not null;
        DownloadLatestButton.Content = releases.FirstOrDefault() is { } latest && latest.Version > UpdateChecker.Current
            ? $"Download {latest.Version}"
            : "Download Latest Release";
    }

    private void OnDownloadLatestClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Releases.FirstOrDefault()?.Download is { } asset) MainViewModel.OpenUrl(asset.Url);
    }

    private void OnRemindClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.RemindMe((sender as MenuItem)?.Tag as string);
        NextRefreshText.Text = _vm.NextRefreshText;
    }

    private static ReleaseItem? ItemOf(object? sender) => (sender as Control)?.DataContext as ReleaseItem;

    private void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.Release.Download is { } asset) MainViewModel.OpenUrl(asset.Url);
    }

    private void OnViewClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) MainViewModel.OpenUrl(item.Release.PageUrl);
    }

    private void OnShowNotesClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm is null) return;
        _vm.ShowReleaseNotes(item.Release);
        Close();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
