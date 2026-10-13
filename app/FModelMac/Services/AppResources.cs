using System.Collections.Concurrent;
using System.Xml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace FModelMac.Services;

// FModel's own PNG icons and .xshd syntax definitions, embedded as Avalonia assets
public static class AppResources
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IHighlightingDefinition?> Highlightings = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Icon(string name) => Icons.GetOrAdd(name, static n =>
    {
        var uri = new Uri($"avares://FModelMac/Assets/Icons/{n}.png");
        return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
    });

    public static IHighlightingDefinition? Highlighting(string? name) => name is null ? null : Highlightings.GetOrAdd(name, static n =>
    {
        var definition = LoadHighlighting(n);
        if (n.Equals("Json", StringComparison.OrdinalIgnoreCase)) JsonThemes.Apply(definition, JsonTheme);
        return definition;
    });

    /// <summary>A new copy of a definition, for an editor that recolors it on its own (the Settings preview).</summary>
    public static IHighlightingDefinition? LoadHighlighting(string name)
    {
        var uri = new Uri($"avares://FModelMac/Assets/Highlighting/{name}.xshd");
        if (!AssetLoader.Exists(uri)) return null;
        using var reader = XmlReader.Create(AssetLoader.Open(uri));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    public static JsonHighlightTheme JsonTheme { get; private set; }

    /// <summary>Raised after the JSON theme changes, so open editors redraw with the new colors.</summary>
    public static event Action? HighlightingChanged;

    /// <summary>FModel's JSON Highlight Theme: recolors the shared JSON definition every editor uses.</summary>
    public static void SetJsonTheme(JsonHighlightTheme theme)
    {
        var changed = theme != JsonTheme;
        JsonTheme = theme;
        if (Highlightings.TryGetValue("Json", out var json)) JsonThemes.Apply(json, theme);
        if (changed) HighlightingChanged?.Invoke();
    }

    // Same mapping as FModel's FolderTreeIconConverter
    public static Bitmap? FolderIcon(string folderName) => folderName.ToLowerInvariant() switch
    {
        "config" => Icon("gear"),
        "plugins" => Icon("puzzle"),
        "windows" => Icon("windows"),
        "android" => Icon("android"),
        "ios" or "mac" => Icon("apple"),
        "linux" or "linuxaarch64" => Icon("linux"),
        "unix" => Icon("unix"),
        "platforms" => Icon("pc"),
        "weapons" => Icon("weapon"),
        "localization" or "internationalization" => Icon("localization"),
        "ui" => Icon("ui"),
        "sounds" or "audio" => Icon("sound"),
        "2dassets" or "textures" => Icon("texture"),
        "engine" or "enginecontent" => Icon("engine"),
        "materials" => Icon("materialicon"),
        "blueprints" => Icon("blueprint"),
        "cinematics" or "movies" => Icon("cinematics"),
        "fortnite" => Icon("fortnite"),
        "fortnitegame" => Icon("fortnitebr"),
        "worldexplorers" => Icon("battlebreakers"),
        "oakgame" => Icon("borderlands"),
        "shootergame" => Icon("valorant"),
        "roguecompany" => Icon("roguecompany"),
        "g3" => Icon("spellbreak"),
        "swgame" => Icon("fallenorder"),
        "prospect" => Icon("thecycle"),
        "creative" => Icon("creative"),
        "athena" => Icon("athena"),
        "stateofdecay2" => Icon("stateofdecay2"),
        _ => null
    };

    // Same mapping as FModel's AssetsListBox item template
    public static Bitmap? FileIcon(string extension) => extension.ToLowerInvariant() switch
    {
        "uasset" => Icon("asset"),
        "ini" => Icon("asset_ini"),
        "png" => Icon("asset_png"),
        "psd" => Icon("asset_psd"),
        _ => Icon("unknown_asset")
    };
}
