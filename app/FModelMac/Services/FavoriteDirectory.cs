namespace FModelMac.Services;

/// <summary>FModel's CustomDirectory: a named shortcut to a folder of the game, saved per game directory.</summary>
public sealed class FavoriteDirectory
{
    public string Header { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>The mounted game has no such folder (worked out when the list is shown, not saved).</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsMissing { get; set; }

    public FavoriteDirectory Clone() => new(Header, Path);

    public FavoriteDirectory() { }

    public FavoriteDirectory(string header, string path)
    {
        Header = header;
        Path = Normalize(path);
    }

    public static string Normalize(string path) => path.Replace('\\', '/').Trim().Trim('/');

    /// <summary>FModel's defaults for the games it knows, by project name.</summary>
    public static List<FavoriteDirectory> Presets(string projectName) => projectName switch
    {
        "FortniteGame" =>
        [
            new("Cosmetics", "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics/"),
            new("Emotes [AUDIO]", "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Sounds/Emotes/"),
            new("Music Packs [AUDIO]", "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Sounds/MusicPacks/"),
            new("Weapons", "FortniteGame/Content/Athena/Items/Weapons/"),
            new("Strings", "FortniteGame/Content/Localization/")
        ],
        "ShooterGame" =>
        [
            new("Audio", "ShooterGame/Content/WwiseAudio/Media/"),
            new("Characters", "ShooterGame/Content/Characters/"),
            new("Gun Buddies", "ShooterGame/Content/Equippables/Buddies/"),
            new("Cards and Sprays", "ShooterGame/Content/Personalization/"),
            new("Shop Backgrounds", "ShooterGame/Content/UI/OutOfGame/MainMenu/Store/Shared/Textures/"),
            new("Weapon Renders", "ShooterGame/Content/UI/Screens/OutOfGame/MainMenu/Collection/Assets/Large/")
        ],
        "DeadByDaylight" =>
        [
            new("Characters V1", "DeadByDaylight/Plugins/DBDCharacters/"),
            new("Characters V2", "DeadByDaylight/Plugins/Runtime/Bhvr/DBDCharacters/"),
            new("Characters (Deprecated)", "DeadbyDaylight/Content/Characters/"),
            new("Meshes", "DeadByDaylight/Content/Meshes/"),
            new("Textures", "DeadByDaylight/Content/Textures/"),
            new("Icons", "DeadByDaylight/Content/UI/UMGAssets/Icons/"),
            new("Blueprints", "DeadByDaylight/Content/Blueprints/"),
            new("Audio Events", "DeadByDaylight/Content/Audio/Events/"),
            new("Audio", "DeadByDaylight/Content/WwiseAudio/Cooked/"),
            new("Data Tables", "DeadByDaylight/Content/Data/"),
            new("Localization", "DeadByDaylight/Content/Localization/")
        ],
        _ => []
    };
}
