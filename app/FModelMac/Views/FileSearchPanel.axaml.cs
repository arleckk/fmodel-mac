using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModelMac.Controls;
using FModelMac.Services;
using FModelMac.ViewModels;

namespace FModelMac.Views;

public sealed record SearchResult(GameFile File)
{
    public string Path => File.Path;
    public string Archive => File is VfsEntry entry ? entry.Vfs.Name : "";
    public string Size => AssetPreview.FormatSize(File.Size);
}

/// <summary>One tab of FModel's SearchView: a list of files filtered by path, with the package context menu.</summary>
public partial class FileSearchPanel : UserControl
{
    private const int MaxResults = 20_000;

    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private GameFile[] _files = [];

    public MainViewModel? Vm { get; set; }

    /// <summary>Shown in red when nothing matches.</summary>
    public string EmptyText { get; set; } = "No packages found matching the query";

    public GameFile[] Files
    {
        get => _files;
        set
        {
            _files = value;
            _ = RunSearchAsync();
        }
    }

    public FileSearchPanel()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RunSearchAsync();
        });
        Query.PropertyChanged += (_, e) =>
        {
            if (e.Property != SearchTextBox.TextProperty) return;
            _debounce.Stop();
            _debounce.Start();
        };
    }

    public void FocusQuery() => Query.FocusInput();

    private void OnOptionChanged(object? sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var files = _files;
        var text = Query.Text?.Trim() ?? "";
        var comparison = MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        Func<string, bool> matches;
        if (UseRegex.IsChecked == true && text.Length > 0)
        {
            try
            {
                var options = MatchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
                var regex = new Regex(text, options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                matches = regex.IsMatch;
            }
            catch (ArgumentException)
            {
                Summary.Text = "Invalid regular expression";
                return;
            }
        }
        else
        {
            matches = path => text.Length == 0 || path.Contains(text, comparison);
        }

        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<SearchResult>();
                var total = 0;
                foreach (var file in files)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!matches(file.Path)) continue;
                    if (++total <= MaxResults) list.Add(new SearchResult(file));
                }
                return (list, total);
            }, cts.Token);

            Results.ItemsSource = found.list;
            Empty.Text = EmptyText;
            Empty.IsVisible = found.total == 0;
            Summary.Text = found.total > MaxResults
                ? $"{found.total:N0} results (showing the first {MaxResults:N0})"
                : $"{found.total:N0} results";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private GameFile? Selected => (Results.SelectedItem as SearchResult)?.File;

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var isPackage = Selected?.IsUePackage == true;
        MetadataItem.IsEnabled = isPackage;
        ReferencesItem.IsEnabled = isPackage;
        DecompileItem.IsEnabled = isPackage;
    }

    private async void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.ExtractAsync(file);
    }

    private async void OnResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || Selected is not { } file) return;
        e.Handled = true;
        await Vm.ExtractAsync(file);
    }

    private async void OnExtractNewTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.ExtractAsync(file, newTab: true);
    }

    private async void OnGoToClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Selected is not { } file) return;
        await Vm.GoToAsync(file);
        (TopLevel.GetTopLevel(this) as Window)?.Owner?.Activate();
    }

    private async void OnShowMetadataClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.ShowMetadataAsync(file);
    }

    private async void OnFindReferencesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.FindReferencesAsync(file);
    }

    private async void OnDecompileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.DecompileAsync(file);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null && Selected is { } file) await Vm.CopyAsync(file, (sender as MenuItem)?.Tag as string);
    }
}
