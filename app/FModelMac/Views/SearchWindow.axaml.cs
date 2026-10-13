using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CUE4Parse.FileProvider.Objects;
using FModelMac.Controls;
using FModelMac.Services;
using FModelMac.ViewModels;

namespace FModelMac.Views;

public sealed record SearchResult(GameFile File)
{
    public string Path => File.Path;
    public string Size => AssetPreview.FormatSize(File.Size);
}

// Packages > Search: every file of the mounted game, filtered by path
public partial class SearchWindow : Window
{
    private const int MaxResults = 20_000;

    private readonly MainViewModel _vm;
    private readonly GameFile[] _files;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;

    // Designer only
    public SearchWindow() : this(new MainViewModel()) { }

    public SearchWindow(MainViewModel vm)
    {
        _vm = vm;
        _files = vm.AllFiles().OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
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
        Opened += (_, _) => Query.FocusInput();
        _ = RunSearchAsync();
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
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
                foreach (var file in _files)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!matches(file.Path)) continue;
                    if (++total <= MaxResults) list.Add(new SearchResult(file));
                }
                return (list, total);
            }, cts.Token);

            Results.ItemsSource = found.list;
            Empty.IsVisible = found.total == 0;
            Summary.Text = found.total > MaxResults
                ? $"{found.total:N0} results (showing the first {MaxResults:N0})"
                : $"{found.total:N0} results";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private SearchResult? Selected => Results.SelectedItem as SearchResult;

    private async void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Selected is { } result) await _vm.ExtractAsync(result.File);
    }

    private async void OnResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Selected is not { } result) return;
        e.Handled = true;
        await _vm.ExtractAsync(result.File);
    }

    private async void OnExtractNewTabClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } result) await _vm.ExtractAsync(result.File, newTab: true);
    }

    private async void OnGoToClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } result) return;
        await _vm.GoToAsync(result.File);
        Owner?.Activate();
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } result && Clipboard is { } clipboard) await clipboard.SetTextAsync(result.Path);
    }
}
