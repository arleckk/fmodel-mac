using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using FModel.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Views;

// FModel's EndpointEditor: an endpoint URL, a JSONPath expression, and a test showing what FModel would read
public partial class EndpointEditorWindow : Window
{
    private const int MaxResponseChars = 2 * 1024 * 1024;

    private readonly EndpointType _type;
    private bool _isValid;
    private bool _loading = true;
    private (string Url, JToken Body)? _lastResponse;

    /// <summary>The configuration to keep: null when the endpoint was disabled.</summary>
    public EndpointConfig? Result { get; private set; }

    // Designer only
    public EndpointEditorWindow() : this(null, EndpointType.Aes) { }

    public EndpointEditorWindow(EndpointConfig? current, EndpointType type)
    {
        _type = type;
        InitializeComponent();
        Title = type == EndpointType.Aes ? "Endpoint Configuration (AES)" : "Endpoint Configuration (Mapping)";
        var json = AppResources.Highlighting("Json");
        EndpointResponse.SyntaxHighlighting = json;
        TargetResponse.SyntaxHighlighting = json;
        InstructionBox.Text = type == EndpointType.Aes ? AesInstructions : MappingInstructions;

        PresetBox.ItemsSource = new[] { "Custom" }.Concat(Endpoints.Presets.Select(p => p.Name)).ToArray();
        UrlBox.Text = current?.Url;
        PathBox.Text = current?.Path;
        PresetBox.SelectedIndex = Math.Max(0, Array.FindIndex(Endpoints.Presets, p => Preset(p).Url == current?.Url) + 1);
        _isValid = current?.IsUsable == true;
        DisableButton.IsVisible = current is not null;
        _loading = false;
        UpdateValidity();
    }

    private EndpointConfig Preset(EndpointPreset preset) => _type == EndpointType.Aes ? preset.Aes : preset.Mapping;

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetBox.SelectedIndex <= 0) return;
        var preset = Preset(Endpoints.Presets[PresetBox.SelectedIndex - 1]);
        _loading = true;
        UrlBox.Text = preset.Url;
        PathBox.Text = preset.Path;
        _loading = false;
        _isValid = false;
        UpdateValidity();
    }

    private void OnConfigChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _isValid = false; // any edit must be tested again, like FModel
        UpdateValidity();
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        await FetchAsync();
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PathBox.Text))
        {
            Show(TargetResponse, "// Write a JSONPath expression first");
            return;
        }
        if (await FetchAsync() is not { } body) return;

        try
        {
            object result = _type == EndpointType.Aes
                ? Endpoints.ReadAes(body, PathBox.Text.Trim())
                : Endpoints.ReadMapping(body, PathBox.Text.Trim());
            _isValid = result is AesResult { IsValid: true } or MappingResult { IsValid: true };
            Show(TargetResponse, JsonConvert.SerializeObject(result, Formatting.Indented));
        }
        catch (Exception ex)
        {
            _isValid = false;
            Show(TargetResponse, $"// {ex.Message}");
        }
        UpdateValidity();
    }

    private async Task<JToken?> FetchAsync()
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            Show(EndpointResponse, "// Write the endpoint URL first (http or https)");
            return null;
        }
        if (_lastResponse is { } cached && cached.Url == url) return cached.Body;

        Show(EndpointResponse, $"// GET {url}…");
        try
        {
            var body = await Endpoints.GetBodyAsync(url);
            _lastResponse = (url, body);
            Show(EndpointResponse, body.ToString(Formatting.Indented));
            return body;
        }
        catch (Exception ex)
        {
            Show(EndpointResponse, $"// {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void Show(AvaloniaEdit.TextEditor editor, string text)
    {
        if (text.Length > MaxResponseChars) text = text[..MaxResponseChars] + "\n… truncated";
        editor.Document = new TextDocument(text);
    }

    private void UpdateValidity()
    {
        ValidityText.Text = _isValid
            ? "Your endpoint configuration is valid! Please, avoid any unnecessary modifications!"
            : "Your endpoint configuration DOES NOT seem to be valid yet! Please, test it out!";
        ValidityText.Foreground = new SolidColorBrush(Color.Parse(_isValid ? "#3CA55C" : "#E5C07B"));
        OkButton.IsEnabled = _isValid;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = new EndpointConfig { Url = UrlBox.Text!.Trim(), Path = PathBox.Text!.Trim(), IsValid = true };
        Close(true);
    }

    private void OnDisableClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSyntaxClick(object? sender, RoutedEventArgs e) => Open("https://support.smartbear.com/alertsite/docs/monitors/api/endpoint/jsonpath.html");

    private void OnEvaluatorClick(object? sender, RoutedEventArgs e) => Open("https://jsonpath.com/");

    private static void Open(string url) => Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });

    private const string AesInstructions =
        "In order to make this work, you first need to understand JSON and its query language. If your game never changes its AES keys or isn't encrypted, you don't need this. " +
        "The AES expression supports up to 2 elements.\n\n" +
        "The first element is mandatory and is assigned to the main AES key. It has to look like a key (hexadecimal, with or without \"0x\"), " +
        "else the configuration isn't valid (the key isn't checked against your files).\n\n" +
        "If your game uses several AES keys, a second element can be the list of dynamic keys: objects with at least \"guid\" (the archive GUID) and \"key\" (its AES key).";

    private const string MappingInstructions =
        "In order to make this work, you first need to understand JSON and its query language. If your game doesn't use unversioned package properties, you don't need this. " +
        "The mapping expression supports up to 2 elements.\n\n" +
        "The first element is mandatory and is the mapping download URL (.usmap with any compression except Brotli, or .jmap).\n\n" +
        "The second element is optional and is the mapping file name; when missing, it's taken from the URL.\n\n" +
        "A local mapping file in Settings takes priority over this endpoint.";
}
