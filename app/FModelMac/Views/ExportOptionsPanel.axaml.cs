using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using FModelMac.Services;

namespace FModelMac.Views;

/// <summary>FModel's ExportOptionsControl, with its enabling rules.</summary>
public partial class ExportOptionsPanel : UserControl
{
    // FModel's mesh formats, glTF first since it needs no add-on
    private static readonly EMeshFormat[] MeshFormats = [EMeshFormat.Gltf2, EMeshFormat.USD, EMeshFormat.UEFormat, EMeshFormat.ActorX];

    public ExportOptionsPanel()
    {
        InitializeComponent();
        Values = new ExportOptionValues();
    }

    public ExportOptionValues Values
    {
        get => new()
        {
            MeshFormat = Read(MeshFormatBox, MeshFormats),
            MeshQuality = Read<EMeshQuality>(MeshQualityBox),
            SocketFormat = Read<ESocketFormat>(SocketFormatBox),
            CompressionFormat = Read<EFileCompressionFormat>(CompressionBox),
            NaniteFormat = Read<ENaniteMeshFormat>(NaniteFormatBox),
            ExportMorphTargets = MorphTargetsBox.IsChecked == true,
            TextureFormat = Read<ETextureFormat>(TextureFormatBox),
            TextureQuality = (int) TextureQualityBox.Value,
            HdrAsHdr = HdrBox.IsChecked == true,
            AllMips = AllMipsBox.IsChecked == true,
            MaterialDepth = Read<EMaterialDepth>(MaterialDepthBox),
            ExportMaterials = MaterialsBox.IsChecked == true
        };
        set
        {
            Fill(MeshFormatBox, MeshFormats, value.MeshFormat);
            Fill(MeshQualityBox, value.MeshQuality);
            Fill(SocketFormatBox, value.SocketFormat);
            Fill(CompressionBox, value.CompressionFormat);
            Fill(NaniteFormatBox, value.NaniteFormat);
            MorphTargetsBox.IsChecked = value.ExportMorphTargets;
            Fill(TextureFormatBox, value.TextureFormat);
            TextureQualityBox.Value = Math.Clamp(value.TextureQuality, 1, 100);
            HdrBox.IsChecked = value.HdrAsHdr;
            AllMipsBox.IsChecked = value.AllMips;
            Fill(MaterialDepthBox, value.MaterialDepth);
            MaterialsBox.IsChecked = value.ExportMaterials;
            UpdateEnabled();
        }
    }

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

    private void OnExportOptionChanged(object? sender, SelectionChangedEventArgs e) => UpdateEnabled();

    private void OnTextureQualityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        // Also raised while the XAML is still loading, before the label exists
        if (TextureQualityText is { } text) text.Text = $"{(int) e.NewValue}";
    }

    // FModel's enabling rules: compression is for UEFormat only, USD always writes PNG textures
    private void UpdateEnabled()
    {
        if (MeshFormatBox.SelectedIndex < 0 || NaniteFormatBox.ItemsSource is null || TextureFormatBox.ItemsSource is null) return;
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
}
