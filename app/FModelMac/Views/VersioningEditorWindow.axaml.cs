using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using FModelMac.Services;
using Newtonsoft.Json;

namespace FModelMac.Views;

public enum VersioningPart { CustomVersions, Options, MapStructTypes }

/// <summary>
/// FModel's DictionaryEditor for the versioning configuration. Unlike FModel it starts from CUE4Parse's effective
/// values for the chosen UE version (so the real option names are there to edit) and keeps only what differs.
/// </summary>
public partial class VersioningEditorWindow : Window
{
    private readonly VersioningPart _part;
    private readonly VersioningSettings _versioning;
    private readonly VersionContainer _defaults;

    // Designer only
    public VersioningEditorWindow() : this(VersioningPart.Options, EGame.GAME_UE5_LATEST, new VersioningSettings()) { }

    /// <param name="versioning">Edited in place when the dialog is confirmed.</param>
    public VersioningEditorWindow(VersioningPart part, EGame game, VersioningSettings versioning)
    {
        _part = part;
        _versioning = versioning;
        _defaults = new VersionContainer(game, versioning.PlatformValue);
        InitializeComponent();
        Editor.SyntaxHighlighting = AppResources.Highlighting("Json");
        Title = part switch
        {
            VersioningPart.CustomVersions => "Versioning Configuration (Custom Versions)",
            VersioningPart.Options => "Versioning Configuration (Options)",
            _ => "Versioning Configuration (MapStructTypes)"
        };
        HelpText.Text = part switch
        {
            VersioningPart.CustomVersions =>
                "Custom versions for games whose packages don't store them (unversioned cooks with engine changes): " +
                "a list of { \"Key\": \"<GUID>\", \"Version\": <number> }. Empty means CUE4Parse's defaults for " + game + ".",
            VersioningPart.Options =>
                "CUE4Parse's serialization switches for " + game + ". Only the values you change are saved, as overrides.",
            _ => "Key and value struct types of map properties that unversioned packages can't describe: " +
                 "\"PropertyName\": { \"Key\": \"KeyType\", \"Value\": \"ValueType\" }. Only new or changed entries are saved."
        };
        Show(Effective());
    }

    private object Effective() => _part switch
    {
        VersioningPart.CustomVersions => _versioning.CustomVersions ?? [],
        VersioningPart.Options => Merge(_defaults.Options, _versioning.Options),
        _ => Merge(_defaults.MapStructTypes, _versioning.MapStructTypes)
    };

    private object Defaults() => _part switch
    {
        VersioningPart.CustomVersions => new List<FCustomVersion>(),
        VersioningPart.Options => Merge(_defaults.Options, null),
        _ => Merge(_defaults.MapStructTypes, null)
    };

    private static SortedDictionary<string, T> Merge<T>(IDictionary<string, T> defaults, IDictionary<string, T>? overrides)
    {
        var merged = new SortedDictionary<string, T>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides ?? new Dictionary<string, T>()) merged[key] = value;
        return merged;
    }

    private void Show(object value) => Editor.Document = new TextDocument(JsonConvert.SerializeObject(value, Formatting.Indented));

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        Show(Defaults());
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = Editor.Document?.Text ?? "";
        try
        {
            switch (_part)
            {
                case VersioningPart.CustomVersions:
                    var versions = JsonConvert.DeserializeObject<List<FCustomVersion>>(text) ?? [];
                    _versioning.CustomVersions = versions.Count > 0 ? versions : null;
                    break;
                case VersioningPart.Options:
                    var options = JsonConvert.DeserializeObject<Dictionary<string, bool>>(text) ?? [];
                    var changed = options.Where(o => !_defaults.Options.TryGetValue(o.Key, out var d) || d != o.Value)
                        .ToDictionary(o => o.Key, o => o.Value);
                    _versioning.Options = changed.Count > 0 ? changed : null;
                    break;
                default:
                    var types = JsonConvert.DeserializeObject<Dictionary<string, KeyValuePair<string, string>>>(text) ?? [];
                    var added = types.Where(t => !_defaults.MapStructTypes.TryGetValue(t.Key, out var d) || !d.Equals(t.Value))
                        .ToDictionary(t => t.Key, t => t.Value);
                    _versioning.MapStructTypes = added.Count > 0 ? added : null;
                    break;
            }
        }
        catch (JsonException ex)
        {
            ErrorText.Text = $"Invalid JSON: {ex.Message}";
            return;
        }
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
