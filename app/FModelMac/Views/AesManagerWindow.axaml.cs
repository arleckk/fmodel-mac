using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using FModelMac.Services;
using FModelMac.ViewModels;

namespace FModelMac.Views;

public sealed partial class DynamicKeyRow : ObservableObject
{
    public required string Guid { get; init; }
    public required string Archives { get; init; }
    [ObservableProperty] private string? _key;
}

public partial class AesManagerWindow : Window
{
    private static readonly Regex KeyPattern = new("^(0x)?[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private const string ZeroGuid = "00000000000000000000000000000000";

    private readonly AppSettings _settings;
    private readonly List<DynamicKeyRow> _rows;

    // Designer only
    public AesManagerWindow() : this(new AppSettings(), []) { }

    public AesManagerWindow(AppSettings settings, IEnumerable<ArchiveItem> archives)
    {
        _settings = settings;
        InitializeComponent();
        MainKey.Text = settings.AesKey;

        // One row per non-zero key GUID used by an encrypted archive, plus keys saved before
        _rows = archives
            .Where(a => a.IsEncrypted && a.Guid != ZeroGuid)
            .GroupBy(a => a.Guid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DynamicKeyRow
            {
                Guid = g.Key,
                Archives = string.Join(", ", g.Select(a => a.Name)),
                Key = settings.DynamicKeys.GetValueOrDefault(g.Key)
            })
            .ToList();
        foreach (var (guid, key) in settings.DynamicKeys)
        {
            if (_rows.All(r => !r.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)))
                _rows.Add(new DynamicKeyRow { Guid = guid, Archives = "(not in this game)", Key = key });
        }

        DynamicList.ItemsSource = _rows;
        NoDynamic.IsVisible = _rows.Count == 0;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var main = Normalize(MainKey.Text);
        if (main is not null && !KeyPattern.IsMatch(main))
        {
            ShowError("The main static key is not a valid AES key");
            return;
        }
        if (_rows.FirstOrDefault(r => Normalize(r.Key) is { } k && !KeyPattern.IsMatch(k)) is { } bad)
        {
            ShowError($"Invalid key for {bad.Guid}");
            return;
        }

        _settings.AesKey = main is null ? null : Prefixed(main);
        _settings.DynamicKeys = _rows
            .Where(r => Normalize(r.Key) is not null)
            .ToDictionary(r => r.Guid, r => Prefixed(Normalize(r.Key)!), StringComparer.OrdinalIgnoreCase);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static string? Normalize(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    private static string Prefixed(string key) => key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key : "0x" + key;
}
