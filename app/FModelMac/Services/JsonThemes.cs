using System.ComponentModel;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace FModelMac.Services;

/// <summary>FModel's JSON Highlight Themes (Settings › Themes).</summary>
public enum JsonHighlightTheme
{
    [Description("Default")] Default,
    [Description("Mint Lavender")] MintLavender,
    [Description("Soft Blue")] SoftBlueGreen,
    [Description("Purple Cyan")] PurpleCyan,
    [Description("Neutral Warm")] NeutralWarm,
    [Description("Nord")] Nord,
    [Description("Mocha")] Mocha,
    [Description("Tokyo Night")] TokyoNight,
    [Description("One Dark")] OneDark,
    [Description("Gruvbox Dark")] GruvboxDark,
    [Description("Rosé Pine")] RosePine,
    [Description("Monokai")] Monokai,
    [Description("Oceanic")] Oceanic,
    [Description("Forest")] Forest,
    [Description("Amber")] Amber,
    [Description("Iceberg")] Iceberg
}

public sealed record HighlightColor(string Foreground, bool Bold = false, bool Italic = false);

public sealed record JsonHighlightPalette(
    HighlightColor FieldName, HighlightColor String, HighlightColor Number, HighlightColor Bool,
    HighlightColor Null, HighlightColor Punctuation, HighlightColor Escape);

/// <summary>FModel's palettes, applied to the named colors of Json.xshd.</summary>
public static class JsonThemes
{
    // Field name, string, number, bool (bold), null (italic), punctuation, escape (bold)
    private static JsonHighlightPalette P(string field, string str, string number, string @bool, string @null, string punctuation, string escape) =>
        new(new HighlightColor(field), new HighlightColor(str), new HighlightColor(number), new HighlightColor(@bool, Bold: true),
            new HighlightColor(@null, Italic: true), new HighlightColor(punctuation), new HighlightColor(escape, Bold: true));

    public static JsonHighlightPalette Get(JsonHighlightTheme theme) => theme switch
    {
        JsonHighlightTheme.MintLavender => P("#CBA6F7", "#8FE3CF", "#FFD166", "#FF9CAC", "#A7B0C0", "#D7DEE9", "#5DD9C1"),
        JsonHighlightTheme.SoftBlueGreen => P("#7CC7FF", "#A6E3A1", "#FFA36C", "#FF6B8A", "#8FA3BF", "#C7D0DD", "#66E3D4"),
        JsonHighlightTheme.PurpleCyan => P("#BD93F9", "#7EE7F2", "#FFB86C", "#FF6B8B", "#9AA7B8", "#CBD5E1", "#5DE4C7"),
        JsonHighlightTheme.NeutralWarm => P("#F8C291", "#A3E635", "#FB923C", "#F87171", "#9CA3AF", "#D1D5DB", "#67E8F9"),
        JsonHighlightTheme.Nord => P("#88C0D0", "#A3BE8C", "#D08770", "#BF616A", "#81A1C1", "#D8DEE9", "#8FBCBB"),
        JsonHighlightTheme.Mocha => P("#CBA6F7", "#A6E3A1", "#FAB387", "#F38BA8", "#9399B2", "#CDD6F4", "#94E2D5"),
        JsonHighlightTheme.TokyoNight => P("#7AA2F7", "#9ECE6A", "#FF9E64", "#F7768E", "#565F89", "#A9B1D6", "#7DCFFF"),
        JsonHighlightTheme.OneDark => P("#E5C07B", "#98C379", "#D19A66", "#61AFEF", "#5C6370", "#ABB2BF", "#56B6C2"),
        JsonHighlightTheme.GruvboxDark => P("#FABD2F", "#B8BB26", "#FE8019", "#FB4934", "#928374", "#EBDBB2", "#8EC07C"),
        JsonHighlightTheme.RosePine => P("#C4A7E7", "#9CCFD8", "#F6C177", "#EB6F92", "#6E6A86", "#E0DEF4", "#31748F"),
        JsonHighlightTheme.Monokai => P("#A6E22E", "#E6DB74", "#AE81FF", "#F92672", "#75715E", "#F8F8F2", "#66D9EF"),
        JsonHighlightTheme.Oceanic => P("#5FB3B3", "#99C794", "#F99157", "#EC5F67", "#65737E", "#D8DEE9", "#6699CC"),
        JsonHighlightTheme.Forest => P("#86EFAC", "#B4D455", "#FACC15", "#FB7185", "#6B7280", "#D1FAE5", "#34D399"),
        JsonHighlightTheme.Amber => P("#FBBF24", "#D9F99D", "#FDBA74", "#F87171", "#A8A29E", "#FDE68A", "#67E8F9"),
        JsonHighlightTheme.Iceberg => P("#84A0C6", "#B4BE82", "#E2A478", "#E27878", "#6B7089", "#D2D4DE", "#89B8C2"),
        _ => P("#FFCB6B", "#C3E88D", "#F78C6C", "#61AFEF", "#7F848E", "#89DDFF", "#4DD0E1")
    };

    public static JsonHighlightTheme Parse(string? name) =>
        Enum.TryParse<JsonHighlightTheme>(name, out var theme) ? theme : JsonHighlightTheme.Default;

    /// <summary>FModel's ApplyJsonTheme: recolors the definition in place; editors using it need a redraw.</summary>
    public static void Apply(IHighlightingDefinition? highlighting, JsonHighlightTheme theme)
    {
        if (highlighting is null) return;
        var palette = Get(theme);
        Apply(highlighting, "FieldName", palette.FieldName);
        Apply(highlighting, "String", palette.String);
        Apply(highlighting, "Number", palette.Number);
        Apply(highlighting, "Bool", palette.Bool);
        Apply(highlighting, "Null", palette.Null);
        Apply(highlighting, "Punctuation", palette.Punctuation);
        Apply(highlighting, "Escape", palette.Escape);
    }

    private static void Apply(IHighlightingDefinition highlighting, string name, HighlightColor color)
    {
        if (highlighting.GetNamedColor(name) is not { } named) return;
        named.Foreground = new SimpleHighlightingBrush(Color.Parse(color.Foreground));
        named.FontWeight = color.Bold ? FontWeight.Bold : null;
        named.FontStyle = color.Italic ? FontStyle.Italic : null;
    }
}
