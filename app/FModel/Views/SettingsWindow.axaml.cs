using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FModel.Controls;
using FModel.Services;
using FModel.Services.Audio;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace FModel.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>True when a change (version, versioning, endpoints, mappings) needs the game to be mounted again.</summary>
    public bool RequiresReload { get; private set; }

    /// <summary>True when the packages language changed: localized texts must be loaded again.</summary>
    public bool LanguageChanged { get; private set; }

    /// <summary>True when the AES endpoint changed: ask it for keys on the next mount whatever "AES Reload" says.</summary>
    public bool AesEndpointChanged { get; private set; }

    private static readonly AesReload[] AesReloads = Enum.GetValues<AesReload>();
    private static readonly JsonHighlightTheme[] JsonThemeValues = Enum.GetValues<JsonHighlightTheme>();
    private readonly HotkeySettings _hotkeys;
    private readonly Dictionary<string, HotkeyBox> _hotkeyBoxes = [];
    private readonly AvaloniaEdit.Highlighting.IHighlightingDefinition? _previewHighlighting = AppResources.LoadHighlighting("Json");

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
        KeepStructureBox.IsChecked = settings.KeepDirectoryStructure;
        PreviewStaticBox.IsChecked = settings.PreviewStaticMeshes;
        PreviewSkeletalBox.IsChecked = settings.PreviewSkeletalMeshes;
        PreviewAnimationsBox.IsChecked = settings.PreviewAnimations;
        PreviewWorldsBox.IsChecked = settings.PreviewWorlds;
        PreviewMaterialsBox.IsChecked = settings.PreviewMaterials;
        PreviewNavMeshesBox.IsChecked = settings.PreviewNavMeshes;
        PreviewTextureSizeSlider.Value = settings.PreviewMaxTextureSize;
        SkeletonAsMeshBox.IsChecked = settings.SaveSkeletonAsMesh;
        AesReloadBox.ItemsSource = AesReloads.Select(r => r.GetDescription()).ToArray();
        AesReloadBox.SelectedIndex = Math.Max(0, Array.IndexOf(AesReloads, settings.AesReload));
        DecompileLuaBox.IsChecked = settings.DecompileLua;
        LuaModeBox.SelectedIndex = settings.LuaMode == LuaMode.Disassemble ? 1 : 0;
        LuaRawStringBox.IsChecked = settings.LuaRawString;
        LuaNoDebugBox.IsChecked = settings.LuaNoDebug;
        LuaLuajBox.IsChecked = settings.LuaLuaj;
        LuaOpCodeMapBox.Text = settings.CurrentLuaOpCodeMap;
        LuaStatusText.Text = LuaDecompiler.IsAvailable
            ? "Compiled Lua 5.0–5.5 chunks (.luac) open as source. unluac is included with FModel."
            : LuaDecompiler.LoadError;
        _hotkeys = settings.Hotkeys.Clone();
        BuildHotkeys();
        JsonPreview.SyntaxHighlighting = _previewHighlighting;
        JsonPreview.Text = JsonPreviewText;
        JsonThemeBox.ItemsSource = JsonThemeValues.Select(t => t.GetDescription()).ToArray();
        JsonThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(JsonThemeValues, settings.JsonHighlightTheme));
        ScriptDataBox.IsChecked = settings.ReadScriptData;
        ShaderMapsBox.IsChecked = settings.ReadShaderMaps;
        ExportImmediatelyBox.IsChecked = settings.ExportImmediately;
        ExportOptions.Values = ExportOptionValues.FromSettings(settings);
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
            : Vgmstream.IsBundled(path) ? $"{version} · included with FModel" : $"{version} · {path}";
        ToolTip.SetTip(VgmstreamText, path);
        VgmstreamButton.IsVisible = version is null;
    }

    private async void OnCopyVgmstreamClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(Vgmstream.InstallCommand);
        VgmstreamText.Text = $"Copied: run '{Vgmstream.InstallCommand}' in Terminal, then reopen Settings";
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

    // ---------- Hotkeys ----------

    private void BuildHotkeys()
    {
        string? section = null;
        foreach (var (name, label, property) in HotkeySettings.Entries)
        {
            if (name != section)
            {
                HotkeysPanel.Children.Add(new SectionSeparator { Text = name, Margin = section is null ? new Thickness(0, 0, 0, 8) : new Thickness(0, 14, 0, 8) });
                section = name;
            }
            var box = new HotkeyBox { Hotkey = _hotkeys.Get(property) };
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == HotkeyBox.HotkeyProperty) _hotkeys.Set(property, box.Hotkey);
            };
            _hotkeyBoxes[property] = box;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,10,*"), Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            Grid.SetColumn(box, 2);
            row.Children.Add(box);
            HotkeysPanel.Children.Add(row);
        }
    }

    private void OnResetHotkeysClick(object? sender, RoutedEventArgs e)
    {
        var defaults = new HotkeySettings();
        foreach (var (property, box) in _hotkeyBoxes) box.Hotkey = defaults.Get(property);
    }

    // ---------- Themes ----------

    private const string JsonPreviewText =
        """
        {
          "title": "This is an example JSON",
          "environment": "production",
          "enabled": true,
          "version": 4,
          "scale": 0.92,
          "features": {
            "previewAssets": true,
            "autoSave": false,
            "maxRecentFiles": 12
          },
          "export": {
            "rootDirectory": "~/Documents/FModel/Output/Exports",
            "keepDirectoryStructure": true,
            "formats": [
              "json",
              "png",
              "wav"
            ]
          },
          "paths": [
            "/Game/Characters/Hero",
            "/Game/UI/Widgets",
            "/Game/Audio/Music"
          ],
          "metadata": {
            "lastOpened": "2026-06-20T14:30:00Z",
            "experimental": false,
            "fallbackTheme": null,
            "escapeExample": "Line one\nLine two\tTabbed \"quoted\"",
            "accentColor": "#FFC857"
          }
        }
        """;

    private JsonHighlightTheme SelectedJsonTheme => JsonThemeValues[Math.Max(0, JsonThemeBox.SelectedIndex)];

    // The preview has its own copy of the definition: open tabs only change on OK
    private void OnJsonThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (JsonPreview is null) return;
        JsonThemes.Apply(_previewHighlighting, SelectedJsonTheme);
        JsonPreview.TextArea.TextView.Redraw();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var usmap = string.IsNullOrWhiteSpace(UsmapBox.Text) ? null : UsmapBox.Text.Trim();
        AesEndpointChanged = !Same(_aesEndpoint, _settings.AesEndpoint);
        RequiresReload = !string.Equals(usmap, _settings.UsmapPath, StringComparison.Ordinal) ||
                         AesEndpointChanged || !Same(_mappingEndpoint, _settings.MappingEndpoint);
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
        _settings.KeepDirectoryStructure = KeepStructureBox.IsChecked == true;
        _settings.PreviewStaticMeshes = PreviewStaticBox.IsChecked == true;
        _settings.PreviewSkeletalMeshes = PreviewSkeletalBox.IsChecked == true;
        _settings.PreviewAnimations = PreviewAnimationsBox.IsChecked == true;
        _settings.PreviewWorlds = PreviewWorldsBox.IsChecked == true;
        _settings.PreviewMaterials = PreviewMaterialsBox.IsChecked == true;
        _settings.PreviewNavMeshes = PreviewNavMeshesBox.IsChecked == true;
        _settings.PreviewMaxTextureSize = (int) PreviewTextureSizeSlider.Value;
        _settings.SaveSkeletonAsMesh = SkeletonAsMeshBox.IsChecked == true;
        _settings.AesReload = AesReloads[Math.Max(0, AesReloadBox.SelectedIndex)];
        _settings.Hotkeys = _hotkeys;
        _settings.DecompileLua = DecompileLuaBox.IsChecked == true;
        _settings.LuaMode = LuaModeBox.SelectedIndex == 1 ? LuaMode.Disassemble : LuaMode.Decompile;
        _settings.LuaRawString = LuaRawStringBox.IsChecked == true;
        _settings.LuaNoDebug = LuaNoDebugBox.IsChecked == true;
        _settings.LuaLuaj = LuaLuajBox.IsChecked == true;
        _settings.CurrentLuaOpCodeMap = LuaOpCodeMapBox.Text;
        _settings.JsonHighlightTheme = SelectedJsonTheme;
        AppResources.SetJsonTheme(SelectedJsonTheme);
        _settings.ReadScriptData = ScriptDataBox.IsChecked == true;
        _settings.ReadShaderMaps = ShaderMapsBox.IsChecked == true;
        _settings.ExportImmediately = ExportImmediatelyBox.IsChecked == true;
        ExportOptions.Values.ApplyTo(_settings);
        _settings.ConvertAudioToWav = AudioFormatBox.SelectedIndex != 1;
        _settings.PlayAudioOnExtract = PlayAudioBox.IsChecked == true;
        _settings.CriwareKey = string.IsNullOrWhiteSpace(CriwareBox.Text) ? null : CriwareBox.Text.Trim();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
