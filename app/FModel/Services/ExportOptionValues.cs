using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace FModel.Services;

/// <summary>
/// FModel's export options as one editable set: the user defaults live in <see cref="AppSettings"/>, and the
/// Export Session can override them for a run (FModel's ExportOptionsViewModel).
/// </summary>
public sealed record ExportOptionValues
{
    public EMeshFormat MeshFormat { get; init; } = EMeshFormat.Gltf2;
    public EMeshQuality MeshQuality { get; init; } = EMeshQuality.Highest;
    public ESocketFormat SocketFormat { get; init; } = ESocketFormat.Bone;
    public EFileCompressionFormat CompressionFormat { get; init; } = EFileCompressionFormat.ZSTD;
    public ENaniteMeshFormat NaniteFormat { get; init; } = ENaniteMeshFormat.NaniteOnly;
    public bool ExportMorphTargets { get; init; } = true;
    public ETextureFormat TextureFormat { get; init; } = ETextureFormat.Png;
    public int TextureQuality { get; init; } = 100;
    public bool HdrAsHdr { get; init; } = true;
    public bool AllMips { get; init; }
    public EMaterialDepth MaterialDepth { get; init; } = EMaterialDepth.TopLayerOnly;
    public bool ExportMaterials { get; init; } = true;

    public static ExportOptionValues FromSettings(AppSettings s) => new()
    {
        MeshFormat = s.MeshExportFormat, MeshQuality = s.MeshQuality, SocketFormat = s.SocketExportFormat,
        CompressionFormat = s.CompressionFormat, NaniteFormat = s.NaniteMeshExportFormat, ExportMorphTargets = s.SaveMorphTargets,
        TextureFormat = s.TextureExportFormat, TextureQuality = s.TextureQuality, HdrAsHdr = s.SaveHdrTexturesAsHdr,
        AllMips = s.ExportAllTextureMips, MaterialDepth = s.MaterialExportFormat, ExportMaterials = s.SaveEmbeddedMaterials
    };

    public void ApplyTo(AppSettings s)
    {
        s.MeshExportFormat = MeshFormat;
        s.MeshQuality = MeshQuality;
        s.SocketExportFormat = SocketFormat;
        s.CompressionFormat = CompressionFormat;
        s.NaniteMeshExportFormat = NaniteFormat;
        s.SaveMorphTargets = ExportMorphTargets;
        s.TextureExportFormat = TextureFormat;
        s.TextureQuality = TextureQuality;
        s.SaveHdrTexturesAsHdr = HdrAsHdr;
        s.ExportAllTextureMips = AllMips;
        s.MaterialExportFormat = MaterialDepth;
        s.SaveEmbeddedMaterials = ExportMaterials;
    }

    /// <summary>FModel's GetExportOptions; the texture platform comes from the game's versioning.</summary>
    public ExportOptions ToOptions(ETexturePlatform platform, EMeshFormat? meshFormat = null, ENaniteMeshFormat? naniteFormat = null) => new(
        meshFormat ?? MeshFormat, naniteFormat ?? NaniteFormat, MeshQuality, platform, TextureFormat, TextureQuality, HdrAsHdr,
        AllMips, MaterialDepth, ExportMaterials, ExportMorphTargets, SocketFormat, CompressionFormat);
}
