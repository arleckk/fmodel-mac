using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FModelMac.Services;
using FModelMac.Services.Audio;

namespace FModelMac.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>True when a change (mappings) needs the game to be mounted again.</summary>
    public bool RequiresReload { get; private set; }

    // Designer only
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        OutputBox.Text = settings.OutputDir;
        UsmapBox.Text = settings.UsmapPath;
        PreviewMeshesBox.IsChecked = settings.PreviewMeshes;
        MeshFormatBox.ItemsSource = MeshFormats.Select(f => f.Label).ToArray();
        MeshFormatBox.SelectedIndex = Math.Max(0, Array.FindIndex(MeshFormats, f => f.Value == settings.MeshExportFormat));
        AudioFormatBox.SelectedIndex = settings.ConvertAudioToWav ? 0 : 1;
        PlayAudioBox.IsChecked = settings.PlayAudioOnExtract;
        CriwareBox.Text = settings.CriwareKey;
        _aesEndpoint = settings.AesEndpoint?.Clone();
        _mappingEndpoint = settings.MappingEndpoint?.Clone();
        ShowEndpoints();
        _ = ShowVgmstreamAsync();
    }

    private EndpointConfig? _aesEndpoint, _mappingEndpoint;

    private void ShowEndpoints()
    {
        static string Describe(EndpointConfig? e) => e is { IsUsable: true } ? e.Url : "Not configured";
        AesEndpointText.Text = Describe(_aesEndpoint);
        MappingEndpointText.Text = Describe(_mappingEndpoint);
        ToolTip.SetTip(AesEndpointText, _aesEndpoint is null ? null : $"{_aesEndpoint.Url}\n{_aesEndpoint.Path}");
        ToolTip.SetTip(MappingEndpointText, _mappingEndpoint is null ? null : $"{_mappingEndpoint.Url}\n{_mappingEndpoint.Path}");
    }

    private async void OnAesEndpointClick(object? sender, RoutedEventArgs e)
    {
        var editor = new EndpointEditorWindow(_aesEndpoint, EndpointType.Aes);
        if (await editor.ShowDialog<bool>(this)) _aesEndpoint = editor.Result;
        ShowEndpoints();
    }

    private async void OnMappingEndpointClick(object? sender, RoutedEventArgs e)
    {
        var editor = new EndpointEditorWindow(_mappingEndpoint, EndpointType.Mapping);
        if (await editor.ShowDialog<bool>(this)) _mappingEndpoint = editor.Result;
        ShowEndpoints();
    }

    private static bool Same(EndpointConfig? a, EndpointConfig? b) =>
        a?.IsUsable == b?.IsUsable && (a is null || b is null || a.Url == b.Url && a.Path == b.Path);

    private async Task ShowVgmstreamAsync()
    {
        var path = Vgmstream.Locate();
        if (path is null)
        {
            VgmstreamText.Text = "Not installed";
            return;
        }
        var version = await Vgmstream.VersionAsync(path);
        VgmstreamText.Text = version is null ? $"{path} doesn't run"
            : Vgmstream.IsBundled(path) ? $"{version} · included with FModelMac" : $"{version} · {path}";
        ToolTip.SetTip(VgmstreamText, path);
        VgmstreamButton.IsVisible = version is null;
    }

    private async void OnCopyVgmstreamClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Vgmstream.InstallCommand);
        VgmstreamText.Text = $"Copied: run '{Vgmstream.InstallCommand}' in Terminal, then reopen Settings";
    }

    // Same choices as FModel's "Mesh Export Format"
    private static readonly (string Value, string Label)[] MeshFormats =
    [
        ("Gltf2", "glTF 2.0 (.glb)"),
        ("USD", "Universal Scene Description (.usda)"),
        ("UEFormat", "UEFormat (.uemodel)"),
        ("ActorX", "ActorX (.psk / .pskx)")
    ];

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output Directory" });
        if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) OutputBox.Text = path;
    }

    private async void OnBrowseUsmapClick(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Mapping File",
            FileTypeFilter = [new FilePickerFileType("Mappings") { Patterns = ["*.usmap"] }]
        });
        if (result.FirstOrDefault()?.TryGetLocalPath() is { } path) UsmapBox.Text = path;
    }

    private void OnClearUsmapClick(object? sender, RoutedEventArgs e) => UsmapBox.Text = "";

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var usmap = string.IsNullOrWhiteSpace(UsmapBox.Text) ? null : UsmapBox.Text.Trim();
        RequiresReload = !string.Equals(usmap, _settings.UsmapPath, StringComparison.Ordinal) ||
                         !Same(_aesEndpoint, _settings.AesEndpoint) || !Same(_mappingEndpoint, _settings.MappingEndpoint);
        _settings.AesEndpoint = _aesEndpoint;
        _settings.MappingEndpoint = _mappingEndpoint;

        if (!string.IsNullOrWhiteSpace(OutputBox.Text)) _settings.OutputDir = OutputBox.Text.Trim();
        _settings.UsmapPath = usmap;
        _settings.PreviewMeshes = PreviewMeshesBox.IsChecked == true;
        _settings.MeshExportFormat = MeshFormats[Math.Max(0, MeshFormatBox.SelectedIndex)].Value;
        _settings.ConvertAudioToWav = AudioFormatBox.SelectedIndex != 1;
        _settings.PlayAudioOnExtract = PlayAudioBox.IsChecked == true;
        _settings.CriwareKey = string.IsNullOrWhiteSpace(CriwareBox.Text) ? null : CriwareBox.Text.Trim();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
