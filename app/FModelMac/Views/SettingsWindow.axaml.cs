using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FModelMac.Services;
using FModelMac.Services.Audio;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace FModelMac.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>True when a change (version, versioning, endpoints, mappings) needs the game to be mounted again.</summary>
    public bool RequiresReload { get; private set; }

    /// <summary>True when the packages language changed: localized texts must be loaded again.</summary>
    public bool LanguageChanged { get; private set; }

    private readonly VersioningSettings _versioning;
    private static readonly string[] UeVersions = Enum.GetNames<EGame>();
    private static readonly ETexturePlatform[] Platforms = Enum.GetValues<ETexturePlatform>();
    private static readonly ELanguage[] Languages = Enum.GetValues<ELanguage>();

    // Designer only
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        OutputBox.Text = settings.OutputDir;
        UsmapBox.Text = settings.UsmapPath;
        PreviewMeshesBox.IsChecked = settings.PreviewMeshes;
        ScriptDataBox.IsChecked = settings.ReadScriptData;
        ShaderMapsBox.IsChecked = settings.ReadShaderMaps;
        ShowExportOptions(settings);
        AudioFormatBox.SelectedIndex = settings.ConvertAudioToWav ? 0 : 1;
        PlayAudioBox.IsChecked = settings.PlayAudioOnExtract;
        CriwareBox.Text = settings.CriwareKey;
        _versioning = settings.CurrentVersioning.Clone();
        UeVersionBox.ItemsSource = UeVersions;
        UeVersionBox.SelectedItem = UeVersions.FirstOrDefault(n => n.Equals(settings.Game, StringComparison.OrdinalIgnoreCase))
                                    ?? nameof(EGame.GAME_UE5_LATEST);
        PlatformBox.ItemsSource = Platforms.Select(p => p.GetDescription()).ToArray();
        PlatformBox.SelectedIndex = Math.Max(0, Array.IndexOf(Platforms, _versioning.PlatformValue));
        LanguageBox.ItemsSource = Languages.Select(l => l.GetDescription()).ToArray();
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.AssetLanguageValue));
        ShowVersioning();
        _aesEndpoint = settings.AesEndpoint?.Clone();
        _mappingEndpoint = settings.MappingEndpoint?.Clone();
        ShowEndpoints();
        _ = ShowVgmstreamAsync();
    }

    private EGame SelectedGame =>
        Enum.TryParse<EGame>(UeVersionBox.SelectedItem as string, out var game) ? game : EGame.GAME_UE5_LATEST;

    private void ShowVersioning()
    {
        var parts = new List<string>();
        if (_versioning.CustomVersions is { Count: > 0 } cv) parts.Add($"{cv.Count} custom version(s)");
        if (_versioning.Options is { Count: > 0 } o) parts.Add($"{o.Count} option override(s)");
        if (_versioning.MapStructTypes is { Count: > 0 } m) parts.Add($"{m.Count} map struct type(s)");
        VersioningText.Text = parts.Count == 0 ? "CUE4Parse's defaults for this UE version" : string.Join(" · ", parts);
    }

    private async Task EditVersioningAsync(VersioningPart part)
    {
        _versioning.Platform = Platforms[Math.Max(0, PlatformBox.SelectedIndex)].ToString();
        await new VersioningEditorWindow(part, SelectedGame, _versioning).ShowDialog<bool>(this);
        ShowVersioning();
    }

    private async void OnCustomVersionsClick(object? sender, RoutedEventArgs e) => await EditVersioningAsync(VersioningPart.CustomVersions);
    private async void OnOptionsClick(object? sender, RoutedEventArgs e) => await EditVersioningAsync(VersioningPart.Options);
    private async void OnMapStructTypesClick(object? sender, RoutedEventArgs e) => await EditVersioningAsync(VersioningPart.MapStructTypes);

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

    // ---------- Export (FModel's export options) ----------

    // FModel's mesh formats, glTF first since it needs no add-on
    private static readonly EMeshFormat[] MeshFormats = [EMeshFormat.Gltf2, EMeshFormat.USD, EMeshFormat.UEFormat, EMeshFormat.ActorX];

    private static void Fill<T>(ComboBox box, T[] values, T selected) where T : struct, Enum
    {
        box.ItemsSource = values.Select(v => v.GetDescription()).ToArray();
        box.SelectedIndex = Math.Max(0, Array.IndexOf(values, selected));
    }

    private static void Fill<T>(ComboBox box, T selected) where T : struct, Enum => Fill(box, Enum.GetValues<T>(), selected);

    private static T Read<T>(ComboBox box, T[]? values = null) where T : struct, Enum
    {
        values ??= Enum.GetValues<T>();
        return values[Math.Clamp(box.SelectedIndex, 0, values.Length - 1)];
    }

    private void ShowExportOptions(AppSettings settings)
    {
        Fill(MeshFormatBox, MeshFormats, settings.MeshExportFormat);
        Fill(MeshQualityBox, settings.MeshQuality);
        Fill(SocketFormatBox, settings.SocketExportFormat);
        Fill(CompressionBox, settings.CompressionFormat);
        Fill(NaniteFormatBox, settings.NaniteMeshExportFormat);
        MorphTargetsBox.IsChecked = settings.SaveMorphTargets;
        Fill(TextureFormatBox, settings.TextureExportFormat);
        TextureQualityBox.Value = Math.Clamp(settings.TextureQuality, 1, 100);
        HdrBox.IsChecked = settings.SaveHdrTexturesAsHdr;
        AllMipsBox.IsChecked = settings.ExportAllTextureMips;
        Fill(MaterialDepthBox, settings.MaterialExportFormat);
        MaterialsBox.IsChecked = settings.SaveEmbeddedMaterials;
        UpdateExportOptions();
    }

    private void OnExportOptionChanged(object? sender, SelectionChangedEventArgs e) => UpdateExportOptions();

    private void OnTextureQualityChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Also raised while the XAML is still loading, before the label exists
        if (TextureQualityText is { } text) text.Text = $"{(int) e.NewValue}";
    }

    // FModel's enabling rules: compression is for UEFormat only, USD always writes PNG textures
    private void UpdateExportOptions()
    {
        if (MeshFormatBox.SelectedIndex < 0) return;
        var format = Read(MeshFormatBox, MeshFormats);
        MeshFormatHint.Text = format switch
        {
            EMeshFormat.Gltf2 => "Opens in Blender and most 3D apps. Animations are saved as USD, since glTF can't hold them.",
            EMeshFormat.USD => "Opens in Blender, Houdini and other USD apps. Textures are always PNG.",
            EMeshFormat.UEFormat => "Needs the UEFormat add-on for Blender or the plugin for Unreal (github.com/h4lfheart/UEFormat).",
            _ => "Needs a PSK/PSA importer, like the Blender add-on at github.com/DarklightGames/io_scene_psk_psa."
        };
        var compression = format == EMeshFormat.UEFormat;
        CompressionLabel.IsEnabled = CompressionBox.IsEnabled = compression;
        CompressionLabel.Opacity = compression ? 1 : 0.5;
        var textures = format != EMeshFormat.USD;
        TextureFormatLabel.IsEnabled = TextureFormatBox.IsEnabled = textures;
        TextureFormatLabel.Opacity = textures ? 1 : 0.5;
        var quality = textures && Read<ETextureFormat>(TextureFormatBox) is ETextureFormat.Jpeg or ETextureFormat.Webp;
        TextureQualityLabel.Opacity = quality ? 1 : 0.5;
        TextureQualityBox.IsEnabled = quality;
        NaniteWarning.IsVisible = Read<ENaniteMeshFormat>(NaniteFormatBox) != ENaniteMeshFormat.NoNanite;
    }

    private void SaveExportOptions()
    {
        _settings.MeshExportFormat = Read(MeshFormatBox, MeshFormats);
        _settings.MeshQuality = Read<EMeshQuality>(MeshQualityBox);
        _settings.SocketExportFormat = Read<ESocketFormat>(SocketFormatBox);
        _settings.CompressionFormat = Read<EFileCompressionFormat>(CompressionBox);
        _settings.NaniteMeshExportFormat = Read<ENaniteMeshFormat>(NaniteFormatBox);
        _settings.SaveMorphTargets = MorphTargetsBox.IsChecked == true;
        _settings.TextureExportFormat = Read<ETextureFormat>(TextureFormatBox);
        _settings.TextureQuality = (int) TextureQualityBox.Value;
        _settings.SaveHdrTexturesAsHdr = HdrBox.IsChecked == true;
        _settings.ExportAllTextureMips = AllMipsBox.IsChecked == true;
        _settings.MaterialExportFormat = Read<EMaterialDepth>(MaterialDepthBox);
        _settings.SaveEmbeddedMaterials = MaterialsBox.IsChecked == true;
    }

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
        _versioning.Platform = Platforms[Math.Max(0, PlatformBox.SelectedIndex)].ToString();
        var game = UeVersionBox.SelectedItem as string ?? _settings.Game;
        RequiresReload |= !game.Equals(_settings.Game, StringComparison.OrdinalIgnoreCase) ||
                          JsonConvert.SerializeObject(_versioning) != JsonConvert.SerializeObject(_settings.CurrentVersioning);
        _settings.Game = game;
        _settings.Versioning[_settings.ArchiveDir ?? ""] = _versioning;
        var language = Languages[Math.Max(0, LanguageBox.SelectedIndex)].ToString();
        LanguageChanged = language != _settings.AssetLanguage;
        _settings.AssetLanguage = language;
        _settings.AesEndpoint = _aesEndpoint;
        _settings.MappingEndpoint = _mappingEndpoint;

        if (!string.IsNullOrWhiteSpace(OutputBox.Text)) _settings.OutputDir = OutputBox.Text.Trim();
        _settings.UsmapPath = usmap;
        _settings.PreviewMeshes = PreviewMeshesBox.IsChecked == true;
        _settings.ReadScriptData = ScriptDataBox.IsChecked == true;
        _settings.ReadShaderMaps = ShaderMapsBox.IsChecked == true;
        SaveExportOptions();
        _settings.ConvertAudioToWav = AudioFormatBox.SelectedIndex != 1;
        _settings.PlayAudioOnExtract = PlayAudioBox.IsChecked == true;
        _settings.CriwareKey = string.IsNullOrWhiteSpace(CriwareBox.Text) ? null : CriwareBox.Text.Trim();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
