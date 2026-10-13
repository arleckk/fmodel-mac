using Avalonia.Media;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Borderlands3.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Assets.Exports;
using CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets;
using CUE4Parse.GameTypes.LegoBatman.Assets;
using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.GameTypes.RED.Assets.Exports;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Exports.Wwise;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Objects;
using CUE4Parse.GameTypes.SquareEnix.UE4.Assets.Exports;
using CUE4Parse.GameTypes.WarnerBros.GothamKnights.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.BuildData;
using CUE4Parse.UE4.Assets.Exports.ChaosClothAsset;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Criware;
using CUE4Parse.UE4.Assets.Exports.Criware.Media;
using CUE4Parse.UE4.Assets.Exports.CustomizableObject;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.FMod;
using CUE4Parse.UE4.Assets.Exports.Foliage;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using CUE4Parse.UE4.Assets.Exports.LevelSequence;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Editor;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.Niagara;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.MediaAssets;
using CUE4Parse.UE4.Objects.Niagara;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace FModel.Services;

/// <summary>
/// FModel's EAssetCategory: a base category in the high 16 bits (what the explorer's filter offers) and a
/// subcategory in the low ones.
/// </summary>
public enum AssetCategory : uint
{
    All = AssetCategories.CategoryBase + (0 << 16),
    Blueprints = AssetCategories.CategoryBase + (1 << 16),
        BlueprintGeneratedClass = Blueprints + 1,
        WidgetBlueprintGeneratedClass = Blueprints + 2,
        AnimBlueprintGeneratedClass = Blueprints + 3,
        RigVMBlueprintGeneratedClass = Blueprints + 4,
        UserDefinedEnum = Blueprints + 5,
        UserDefinedStruct = Blueprints + 6,
        Blueprint = Blueprints + 8,
        CookedMetaData = Blueprints + 9,
    Mesh = AssetCategories.CategoryBase + (2 << 16),
        StaticMesh = Mesh + 1,
        SkeletalMesh = Mesh + 2,
        CustomizableObject = Mesh + 3,
        NaniteDisplacedMesh = Mesh + 4,
        GeometryCollection = Mesh + 5,
        ChaosClothAsset = Mesh + 6,
    Texture = AssetCategories.CategoryBase + (3 << 16),
    Materials = AssetCategories.CategoryBase + (4 << 16),
        Material = Materials + 1,
        MaterialEditorData = Materials + 2,
        MaterialFunction = Materials + 3,
        MaterialFunctionEditorData = Materials + 4,
        MaterialParameterCollection = Materials + 5,
    Animation = AssetCategories.CategoryBase + (5 << 16),
        Skeleton = Animation + 1,
        Rig = Animation + 2,
    Level = AssetCategories.CategoryBase + (6 << 16),
        World = Level + 1,
        BuildData = Level + 2,
        LevelSequence = Level + 3,
        Foliage = Level + 4,
    Data = AssetCategories.CategoryBase + (7 << 16),
        ItemDefinitionBase = Data + 1,
        CurveBase = Data + 2,
        PhysicsAsset = Data + 3,
        ObjectRedirector = Data + 4,
        PhysicalMaterial = Data + 5,
        ByteCode = Data + 6,
    Media = AssetCategories.CategoryBase + (8 << 16),
        Audio = Media + 1,
        Video = Media + 2,
        Font = Media + 3,
        SoundBank = Media + 4,
        AudioEvent = Media + 5,
    Particle = AssetCategories.CategoryBase + (9 << 16),
    GameSpecific = AssetCategories.CategoryBase + (10 << 16),
        Borderlands = GameSpecific + 1,
        Aion2 = GameSpecific + 2,
        RocoKingdomWorld = GameSpecific + 3,
        DeltaForce = GameSpecific + 4,
        LegoBatman = GameSpecific + 5,
        ArcSys = GameSpecific + 6,
        GothamKnights = GameSpecific + 7,
        Nascar = GameSpecific + 8
}

/// <summary>FModel's EAssetFamily: audio middleware and Houdini get their own icon whatever the category.</summary>
public enum AssetFamily { None, Criware, Wwise, FMod, Houdini }

/// <summary>What the explorer shows of a file: its category, the type of its main export and a thumbnail.</summary>
/// <param name="Thumbnail">PNG, at most <see cref="AssetCategories.ThumbnailSize"/> pixels wide, for textures and images.</param>
/// <param name="Layers">Texture arrays: how many layers.</param>
public sealed record AssetInfo(AssetCategory Category, AssetFamily Family, string TypeName, byte[]? Thumbnail = null, int Layers = 0);

/// <summary>FModel's GameFileViewModel resolution and FileToGeometryConverter, for the asset explorer.</summary>
public static class AssetCategories
{
    public const int ThumbnailSize = 128;
    public const uint CategoryBase = 0x00010000;

    public static AssetCategory BaseOf(this AssetCategory category) => (AssetCategory) ((uint) category & 0xFFFF0000);

    public static bool IsOf(this AssetCategory item, AssetCategory category) => item.BaseOf() == category.BaseOf();

    /// <summary>What the explorer's category filter offers: All, Blueprints, Mesh, Texture…</summary>
    public static readonly AssetCategory[] BaseCategories =
        Enum.GetValues<AssetCategory>().Where(c => c == c.BaseOf()).ToArray();

    /// <summary>The category without opening the file: what the extension tells (packages say "uasset" until resolved).</summary>
    public static AssetInfo FromExtension(GameFile file, EGame game) => file.Extension.ToLowerInvariant() switch
    {
        "umap" => new AssetInfo(AssetCategory.World, AssetFamily.None, "World"),
        _ when file.IsUePackage && file.NameWithoutExtension.EndsWith("_BuiltData") =>
            new AssetInfo(AssetCategory.BuildData, AssetFamily.None, "MapBuildDataRegistry"),
        var ext => new AssetInfo(ExtensionCategory(ext, game), ExtensionFamily(ext), ext)
    };

    /// <summary>
    /// Opens the package to find the class of its main export (only constructed, not read) and, when asked, a thumbnail
    /// of its texture (FModel's ResolveByPackageAsync). Other files go by their extension; images get a thumbnail.
    /// </summary>
    public static AssetInfo Resolve(IFileProvider provider, GameFile file, bool thumbnail, ETexturePlatform platform)
    {
        var game = provider.Versions.Game;
        var info = FromExtension(file, game);
        if (!file.IsUePackage)
        {
            return thumbnail && info.Category == AssetCategory.Texture && file.Extension.ToLowerInvariant() is "png" or "jpg" or "jpeg" or "bmp"
                ? info with { Thumbnail = ImageThumbnail(file.Read()) }
                : info;
        }
        if (file.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase) || info.Category == AssetCategory.BuildData) return info;

        var package = provider.LoadPackage(file);
        var main = package.GetExportIndex(file.NameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        if (main < 0) main = package.GetExportIndex($"{file.NameWithoutExtension}_C", StringComparison.OrdinalIgnoreCase);
        if (main < 0) main = 0;
        var pointer = new FPackageIndex(package, main + 1).ResolvedObject;
        if (pointer?.Object is null || package is not AbstractUePackage abstractPackage) return info;

        var dummy = abstractPackage.ConstructObject(pointer.Class, package);
        var (category, family) = (Category(dummy, game), Family(dummy));
        info = new AssetInfo(category, family, dummy.ExportType);
        if (!thumbnail) return info;

        switch (category)
        {
            case AssetCategory.Texture when pointer.Object.Value is UTexture texture:
                var layers = texture is UTexture2DArray array && array.GetFirstMip() is { SizeZ: > 1 } mip ? mip.SizeZ : 0;
                return info with { Thumbnail = TextureThumbnail(texture, platform), Layers = layers };
            case AssetCategory.ItemDefinitionBase when pointer.Object.Value is UItemDefinitionBase item:
                // Fortnite cosmetics: their icon
                var icon = ItemIcon(item.DataList);
                if (icon is null && item is UAthenaPickaxeItemDefinition pickaxe && pickaxe.WeaponDefinition.TryLoad(out UItemDefinitionBase weapon))
                    icon = ItemIcon(weapon.DataList);
                return info with { Thumbnail = icon is null ? null : TextureThumbnail(icon, platform) };
        }
        return info;
    }

    private static UTexture2D? ItemIcon(FInstancedStruct[] dataList)
    {
        foreach (var data in dataList)
            if (data.NonConstStruct?.TryGetValue(out FSoftObjectPath icon, "Icon", "LargeIcon") == true && icon.TryLoad<UTexture2D>(out var texture))
                return texture;
        return null;
    }

    private static byte[]? TextureThumbnail(UTexture texture, ETexturePlatform platform)
    {
        var decoded = texture is UTextureCube ? texture.Decode(platform)?.ToPanorama() : texture.Decode(ThumbnailSize, platform);
        if (decoded is null) return null;
        using var bitmap = decoded.ToSkBitmap();
        return Shrink(bitmap);
    }

    private static byte[]? ImageThumbnail(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null ? null : Shrink(bitmap);
    }

    private static byte[] Shrink(SKBitmap bitmap)
    {
        var scale = Math.Min(1f, (float) ThumbnailSize / Math.Max(bitmap.Width, bitmap.Height));
        if (scale >= 1f) return bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        using var small = bitmap.Resize(new SKImageInfo(Math.Max(1, (int) (bitmap.Width * scale)), Math.Max(1, (int) (bitmap.Height * scale))),
            SKFilterQuality.Medium);
        return (small ?? bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static AssetFamily Family(UObject dummy) => dummy switch
    {
        UAtomSoundBase or UAtomSoundBank or USoundAtomCue or USoundAtomCueSheet
            or USoundAtomConfig or UAtomDspBusSetting or UAtomRackBase => AssetFamily.Criware,
        UAkAudioType or UAkAssetData or UAkAssetPlatformData or UAkMediaAssetData
            or UAkMediaAsset or UWwiseAssetLibrary or UExternalSource or UExternalSourceBank => AssetFamily.Wwise,
        UFMODEvent or UFMODBank or UFMODBankLookup or UFMODBus
            or UFMODSnapshot or UFMODSnapshotReverb or UFMODVCA => AssetFamily.FMod,
        UHoudiniAsset or UHoudiniStaticMesh => AssetFamily.Houdini,
        _ => AssetFamily.None
    };

    // FModel's switch, in its order (a subclass must come before its parent)
    private static AssetCategory Category(UObject dummy, EGame game) => dummy switch
    {
        URigVMBlueprintGeneratedClass => AssetCategory.RigVMBlueprintGeneratedClass,
        UAnimBlueprintGeneratedClass => AssetCategory.AnimBlueprintGeneratedClass,
        UWidgetBlueprintGeneratedClass => AssetCategory.WidgetBlueprintGeneratedClass,
        UBlueprintGeneratedClass or UFunction => AssetCategory.BlueprintGeneratedClass,
        UUserDefinedEnum => AssetCategory.UserDefinedEnum,
        UUserDefinedStruct => AssetCategory.UserDefinedStruct,
        UBlueprintCore => AssetCategory.Blueprint,
        UClassCookedMetaData or UStructCookedMetaData or UEnumCookedMetaData => AssetCategory.CookedMetaData,

        UStaticMesh or UHoudiniStaticMesh => AssetCategory.StaticMesh,
        USkeletalMesh => AssetCategory.SkeletalMesh,
        UChaosClothAsset => AssetCategory.ChaosClothAsset,
        UCustomizableObject => AssetCategory.CustomizableObject,
        UNaniteDisplacedMesh => AssetCategory.NaniteDisplacedMesh,
        UGeometryCollection => AssetCategory.GeometryCollection,
        UHoudiniAsset => AssetCategory.Data,

        UTexture => AssetCategory.Texture,

        UMaterialInterface => AssetCategory.Material,
        UMaterialInterfaceEditorOnlyData => AssetCategory.MaterialEditorData,
        UMaterialFunction => AssetCategory.MaterialFunction,
        UMaterialFunctionEditorOnlyData => AssetCategory.MaterialFunctionEditorData,
        UMaterialParameterCollection => AssetCategory.MaterialParameterCollection,

        UAnimationAsset => AssetCategory.Animation,
        USkeleton => AssetCategory.Skeleton,
        URig => AssetCategory.Rig,

        UWorld => AssetCategory.World,
        UMapBuildDataRegistry => AssetCategory.BuildData,
        ULevelSequence => AssetCategory.LevelSequence,
        UFoliageType => AssetCategory.Foliage,

        UItemDefinitionBase => AssetCategory.ItemDefinitionBase,
        UDataAsset or UDataTable or UCurveTable or UStringTable => AssetCategory.Data,
        UCurveBase => AssetCategory.CurveBase,
        UPhysicsAsset => AssetCategory.PhysicsAsset,
        UObjectRedirector => AssetCategory.ObjectRedirector,
        UPhysicalMaterial => AssetCategory.PhysicalMaterial,

        USoundAtomCue or UAtomSoundCue or UAkAudioEvent or USoundCue or UFMODEvent
            or UAkAssetData or UAkAssetPlatformData => AssetCategory.AudioEvent,
        UFileManaMovie => AssetCategory.Video,
        UFMODBankLookup or USoundAtomConfig or UFMODSnapshotReverb or UFMODBus or UFMODSnapshot
            or UFMODVCA or UAtomDspBusSetting => AssetCategory.Data,
        USQEXSEADSoundAttenuation => AssetCategory.Audio,
        UFMODBank or UAkAudioBank or UAtomSoundBank or USoundAtomCueSheet
            or UAkInitBank or USQEXSEADSoundBank => AssetCategory.SoundBank,
        UWwiseAssetLibrary or UAtomSoundBase or USoundBase or UAkMediaAssetData
            or UExternalSource or UExternalSourceBank or UAkMediaAsset => AssetCategory.Audio,
        UAkAudioType => AssetCategory.Data,

        UFileMediaSource => AssetCategory.Video,
        UFont or UFontFace or USMGLocaleFontUMG => AssetCategory.Font,
        UNiagaraSystem or UNiagaraScriptBase or UParticleSystem => AssetCategory.Particle,

        // Game specific
        UBorderlandsDialogObject when game is EGame.GAME_Borderlands3 => AssetCategory.Borderlands,
        UGbxGraphAsset or UDialogScriptData or UDialogPerformanceData when game is EGame.GAME_Borderlands4 or EGame.GAME_Borderlands3 => AssetCategory.Borderlands,
        UFaceFXAnimSet when game is EGame.GAME_Borderlands4 => AssetCategory.Borderlands,
        UWubAudioEvent or UWubDialogueEvent when game is EGame.GAME_LEGOBatmanLegacyoftheDarkKnight => AssetCategory.LegoBatman,
        UREDBinaryObject => AssetCategory.ArcSys,
        UOrpheusBank or UOrpheusEvent => AssetCategory.GothamKnights,
        UIRMesh => AssetCategory.Nascar,

        _ => AssetCategory.All
    };

    private static AssetFamily ExtensionFamily(string ext) => ext switch
    {
        "acb" or "acf" or "adx" or "awb" or "hca" or "usm" => AssetFamily.Criware,
        "bnk" or "pck" or "wem" => AssetFamily.Wwise,
        "bank" or "fsb" => AssetFamily.FMod,
        "hda" or "hip" => AssetFamily.Houdini,
        _ => AssetFamily.None
    };

    private static AssetCategory ExtensionCategory(string ext, EGame game) => ext switch
    {
        "uproject" or "uefnproject" or "upluginmanifest" or "uplugin" or "ini" or "locmeta" or "locres" or "verse" or "lua" or "luac"
            or "json5" or "json" or "bin" or "txt" or "log" or "pem" or "xml" or "gitignore" or "gitattributes" or "html" or "css"
            or "js" or "data" or "csv" or "sql" or "py" or "cs" or "acf" => AssetCategory.Data,
        "stinfo" or "ushaderbytecode" or "upipelinecache" or "dxbc" => AssetCategory.ByteCode,
        "wav" or "awb" or "adx" or "hca" or "xvag" or "flac" or "at9" or "wem" or "ogg" => AssetCategory.Audio,
        "acb" or "bank" or "fsb" or "bnk" or "pck" => AssetCategory.SoundBank,
        "ufont" or "otf" or "ttf" => AssetCategory.Font,
        "webm" or "mp4" or "usm" => AssetCategory.Video,
        "jpg" or "jpeg" or "png" or "bmp" or "svg" => AssetCategory.Texture,
        "ace" when game is EGame.GAME_Borderlands3 => AssetCategory.Borderlands,
        "ncs" when game is EGame.GAME_Borderlands4 => AssetCategory.Borderlands,
        "dat" when game is EGame.GAME_Aion2 => AssetCategory.Aion2,
        "bytes" or "non" or "cam" when game is EGame.GAME_RocoKingdomWorld => AssetCategory.RocoKingdomWorld,
        "ustbin" when game is EGame.GAME_DeltaForce => AssetCategory.DeltaForce,
        _ => AssetCategory.All
    };

    // ---------- Icons (FModel's FileToGeometryConverter / FolderToGeometryConverter and Colors.xaml) ----------

    /// <summary>The icon (a key of Themes/Icons.axaml) and its color for a file.</summary>
    public static (string Icon, Color Color) IconFor(AssetCategory category, string typeName, AssetFamily family)
    {
        var (icon, color) = category switch
        {
            AssetCategory.Blueprint or AssetCategory.BlueprintGeneratedClass => ("BlueprintIcon", Colors.DodgerBlue),
            AssetCategory.WidgetBlueprintGeneratedClass => ("BlueprintIcon", Colors.DarkViolet),
            AssetCategory.AnimBlueprintGeneratedClass => ("BlueprintIcon", Colors.Crimson),
            AssetCategory.RigVMBlueprintGeneratedClass => ("BlueprintIcon", Colors.Teal),
            AssetCategory.UserDefinedEnum => ("BlueprintIcon", Colors.DarkGoldenrod),
            AssetCategory.UserDefinedStruct => ("BlueprintIcon", Colors.Tan),
            AssetCategory.CookedMetaData => ("BlueprintIcon", Colors.Yellow),

            AssetCategory.Texture => ("TextureIconAlt", Colors.MediumPurple),

            AssetCategory.StaticMesh => ("StaticMeshIconAlt", Colors.White),
            AssetCategory.SkeletalMesh => ("SkeletalMeshIconAlt", Colors.White),
            AssetCategory.ChaosClothAsset => ("ChaosClothIcon", Colors.LightSlateGray),
            AssetCategory.CustomizableObject => ("StaticMeshIconAlt", Colors.DarkRed),
            AssetCategory.NaniteDisplacedMesh => ("StaticMeshIconAlt", Colors.ForestGreen),
            AssetCategory.GeometryCollection => ("GeometryCollectionIcon", Colors.White),

            AssetCategory.Material => ("MaterialIcon", Colors.BurlyWood),
            AssetCategory.MaterialEditorData => ("MaterialIcon", Colors.Yellow),
            AssetCategory.MaterialParameterCollection => ("MaterialParameterCollectionIcon", Colors.BurlyWood),
            AssetCategory.MaterialFunction => ("MaterialFunctionIcon", Colors.BurlyWood),
            AssetCategory.MaterialFunctionEditorData => ("MaterialFunctionIcon", Colors.Yellow),

            AssetCategory.Animation => ("AnimationIconAlt", Colors.Coral),
            AssetCategory.Skeleton => ("SkeletonIcon", Colors.White),
            AssetCategory.Rig => ("AnimationIconAlt", Colors.White),

            AssetCategory.World => ("WorldIcon", Colors.Orange),
            AssetCategory.BuildData => ("MapIconAlt", Colors.Tomato),
            AssetCategory.LevelSequence => ("ClapperIcon", Colors.Coral),
            AssetCategory.Foliage => ("FoliageIcon", Colors.ForestGreen),

            AssetCategory.ItemDefinitionBase => ("DataTableIcon", Colors.White),
            AssetCategory.CurveBase => ("CurveIcon", Colors.HotPink),
            AssetCategory.PhysicsAsset => ("PhysicsIcon", Colors.White),
            AssetCategory.ObjectRedirector => ("RedirectorIcon", Colors.LightSlateGray),
            AssetCategory.PhysicalMaterial => ("MaterialIcon", Colors.White),

            AssetCategory.Audio => ("AudioIconAlt", Colors.MediumSeaGreen),
            AssetCategory.SoundBank => ("AudioIconAlt", Colors.DarkSeaGreen),
            AssetCategory.AudioEvent => ("AudioIconAlt", Colors.LightSeaGreen),
            AssetCategory.Video => ("VideoIcon", Colors.IndianRed),
            AssetCategory.Font => ("FontIcon", Colors.White),
            AssetCategory.Particle => ("ParticleIcon", Colors.Gold),

            AssetCategory.Data => typeName.ToLowerInvariant() switch
            {
                "uplugin" or "upluginmanifest" => ("PluginIcon", Colors.GreenYellow),
                "uproject" or "uefnproject" => ("PluginIcon", Colors.DeepSkyBlue),
                "ini" => ("ConfigIcon", Colors.LightSlateGray),
                "locmeta" or "locres" => ("LocaleIcon", Colors.CornflowerBlue),
                "lua" or "luac" => ("LuaIcon", Colors.Blue),
                "json5" or "json" => ("JsonIcon", Colors.LightGreen),
                "txt" or "log" => ("TxtIcon", Colors.White),
                "pem" => ("CertificateIcon", Colors.White),
                "verse" => ("VerseIcon", Colors.White),
                "function" => ("FunctionIcon", Colors.White),
                "bin" => ("DataTableIcon", Colors.Yellow),
                "xml" => ("XmlIcon", Colors.LightGreen),
                "gitignore" or "gitattributes" => ("GitIcon", Colors.Coral),
                "html" => ("HtmlIcon", Colors.Tomato),
                "js" => ("JavaScriptIcon", Colors.Yellow),
                "css" => ("CssIcon", Colors.MediumPurple),
                "csv" => ("CsvIcon", Colors.ForestGreen),
                "sql" => ("SQLIcon", Color.Parse("#00758f")),
                "py" => ("PythonIcon", Color.Parse("#3d77a8")),
                "cs" => ("CSharpIcon", Color.Parse("#9e559a")),
                _ => ("DataTableIcon", Colors.White)
            },
            AssetCategory.ByteCode => ("CodeIcon", Colors.SandyBrown),

            AssetCategory.Borderlands => ("BorderlandsIcon", Colors.Yellow),
            AssetCategory.Aion2 => ("AionIcon", Colors.DeepSkyBlue),
            AssetCategory.RocoKingdomWorld => ("RocoKingdomWorldIcon", Color.Parse("#fecf4d")),
            AssetCategory.DeltaForce => ("DeltaForceIcon", Colors.LightGreen),
            AssetCategory.LegoBatman or AssetCategory.GothamKnights => ("BatmanIcon", Colors.Gold),
            AssetCategory.ArcSys => ("ArcSysIcon", Colors.Red),
            AssetCategory.Nascar => ("NascarIcon", Color.Parse("#dd002a")),

            _ => ("AssetIcon", Colors.White)
        };

        return (family, category) switch
        {
            (AssetFamily.Criware, AssetCategory.Data) => ("CriwareIcon", Color.Parse("#005bab")),
            (AssetFamily.Criware, _) => ("CriwareIcon", color),
            (AssetFamily.Wwise, AssetCategory.Data) => ("WwiseIcon", Color.Parse("#0152a1")),
            (AssetFamily.Wwise, _) => ("WwiseIcon", color),
            (AssetFamily.FMod, AssetCategory.Data) => ("FModIcon", Color.Parse("#00758f")),
            (AssetFamily.FMod, _) => ("FModIcon", color),
            (AssetFamily.Houdini, _) => ("HoudiniIcon", Color.Parse("#ff6600")),
            _ => (icon, color)
        };
    }

    /// <summary>The small badge a folder tile gets from its name (Textures, Audio, Maps…), if any.</summary>
    public static (string Icon, Color Color)? FolderIconFor(string name) => name.ToLowerInvariant() switch
    {
        "ai" => ("AIIcon", Colors.LightGray),
        "textures" or "texture" or "ui" or "icons" or "umgassets" or "hud" or "hdri" or "tex" => ("TextureIconAlt", Colors.MediumPurple),
        "config" or "tags" => ("ConfigIcon", Colors.LightSlateGray),
        "audio" or "wwiseaudio" or "wwise" or "fmod" or "sound" or "sounds" or "cue" => ("AudioIconAlt", Colors.MediumSeaGreen),
        "soundbanks" or "banks" => ("AudioIconAlt", Colors.DarkSeaGreen),
        "audioevent" or "audioevents" => ("AudioIconAlt", Colors.LightSeaGreen),
        "movies" or "video" or "videos" or "cinematics" => ("VideoIcon", Colors.IndianRed),
        "data" or "datatable" or "datatables" => ("DataTableIcon", Colors.SteelBlue),
        "curves" => ("CurveIcon", Colors.HotPink),
        "bp" or "blueprint" or "blueprints" or "audioblueprints" => ("BlueprintIcon", Colors.DodgerBlue),
        "staticmesh" or "mesh" or "meshes" or "model" or "models" or "characters" or "environment" or "props" => ("StaticMeshIconAlt", Colors.White),
        "material" or "materials" or "materialinstance" or "mastermaterial" => ("MaterialIcon", Colors.BurlyWood),
        "materialfunctions" or "materialfunction" => ("MaterialFunctionIcon", Colors.BurlyWood),
        "plugin" or "plugins" => ("PluginIcon", Colors.GreenYellow),
        "localization" or "locale" or "l10n" => ("LocaleIcon", Colors.CornflowerBlue),
        "map" or "maps" or "world" or "worlds" => ("WorldIcon", Colors.Orange),
        "effect" or "effects" or "niagara" or "vfx" or "particlesystems" or "particles" => ("ParticleIcon", Colors.Gold),
        "animation" or "animations" or "anim" or "animsequences" or "animsequence" or "montage" or "montages" => ("AnimationIconAlt", Colors.Coral),
        "physics" or "physicasset" or "physicassets" => ("PhysicsIcon", Colors.White),
        "windows" => ("MonitorIcon", Colors.White),
        "skeleton" or "skeletons" => ("SkeletonIcon", Colors.White),
        "certificate" or "certificates" => ("CertificateIcon", Colors.White),
        "fonts" or "font" => ("FontIcon", Colors.White),
        _ => null
    };
}
