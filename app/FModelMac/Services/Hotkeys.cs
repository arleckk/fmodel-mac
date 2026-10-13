using System.Text;
using Avalonia.Input;
using Newtonsoft.Json;

namespace FModelMac.Services;

/// <summary>A key and its modifiers (FModel's Hotkey). <see cref="Key.None"/> means unassigned.</summary>
[JsonConverter(typeof(HotkeyJsonConverter))]
public sealed record Hotkey(Key Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public static Hotkey None { get; } = new(Key.None);

    [JsonIgnore]
    public bool IsAssigned => Key != Key.None;

    // Exact modifiers, unlike FModel: on macOS ⌘A, ⌘C… must not also trigger the plain A, C… hotkeys
    public bool IsTriggered(KeyEventArgs e) => IsAssigned && e.Key == Key && e.KeyModifiers == Modifiers;

    /// <summary>macOS style: ⌃⌥⇧⌘ then the key.</summary>
    public override string ToString()
    {
        if (!IsAssigned) return "";
        var text = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Control)) text.Append('⌃');
        if (Modifiers.HasFlag(KeyModifiers.Alt)) text.Append('⌥');
        if (Modifiers.HasFlag(KeyModifiers.Shift)) text.Append('⇧');
        if (Modifiers.HasFlag(KeyModifiers.Meta)) text.Append('⌘');
        return text.Append(KeyName(Key)).ToString();
    }

    public static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char) ('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (char) ('0' + (key - Key.NumPad0)),
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        Key.Enter => "↩", Key.Tab => "⇥", Key.Space => "Space",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemQuestion => "/", Key.OemPipe or Key.OemBackslash => "\\", Key.OemTilde => "`",
        Key.Add => "Num+", Key.Subtract => "Num-", Key.Multiply => "Num*", Key.Divide => "Num/", Key.Decimal => "Num.",
        _ => key.ToString()
    };

    /// <summary>Keys that can't be a hotkey on their own: modifiers and the keys that clear the editor.</summary>
    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.CapsLock or Key.None;
}

/// <summary>
/// Saved as text ("Meta+Shift+P", "None"): Newtonsoft would otherwise fill the existing (shared) value in place.
/// </summary>
public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override void WriteJson(JsonWriter writer, Hotkey? value, JsonSerializer serializer)
    {
        if (value is null || !value.IsAssigned) writer.WriteValue(nameof(Key.None));
        else if (value.Modifiers == KeyModifiers.None) writer.WriteValue(value.Key.ToString());
        else writer.WriteValue($"{value.Modifiers.ToString().Replace(", ", "+")}+{value.Key}");
    }

    public override Hotkey ReadJson(JsonReader reader, Type objectType, Hotkey? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.String || reader.Value is not string text) return Hotkey.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], out var key)) return Hotkey.None;
        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
            if (Enum.TryParse<KeyModifiers>(part, out var modifier)) modifiers |= modifier;
        return new Hotkey(key, modifiers);
    }
}

/// <summary>
/// FModel's Settings › Hotkeys, with ⌘ instead of Ctrl. The single-letter ones only fire when no text field has
/// the focus, like in FModel.
/// </summary>
public sealed class HotkeySettings
{
    public Hotkey DirLeftTab { get; set; } = new(Key.A);
    public Hotkey DirRightTab { get; set; } = new(Key.D);
    public Hotkey AssetLeftTab { get; set; } = new(Key.Q);
    public Hotkey AssetRightTab { get; set; } = new(Key.E);
    public Hotkey AssetAddTab { get; set; } = new(Key.T, KeyModifiers.Meta);
    public Hotkey AssetRemoveTab { get; set; } = new(Key.W, KeyModifiers.Meta);
    public Hotkey SwitchAssetExplorer { get; set; } = new(Key.Z);

    public Hotkey ExportData { get; set; } = Hotkey.None;
    public Hotkey ExportProperties { get; set; } = Hotkey.None;
    public Hotkey ExportTextures { get; set; } = Hotkey.None;
    public Hotkey ExportModels { get; set; } = Hotkey.None;
    public Hotkey ExportWorlds { get; set; } = Hotkey.None;
    public Hotkey ExportAnimations { get; set; } = Hotkey.None;
    public Hotkey ExportAudio { get; set; } = Hotkey.None;
    public Hotkey ExportCode { get; set; } = Hotkey.None;

    public Hotkey AddAudio { get; set; } = new(Key.N, KeyModifiers.Meta);
    public Hotkey PlayPauseAudio { get; set; } = new(Key.K);
    public Hotkey PreviousAudio { get; set; } = new(Key.J);
    public Hotkey NextAudio { get; set; } = new(Key.L);
    public Hotkey RemoveAudio { get; set; } = new(Key.X);

    /// <summary>What Settings › Hotkeys lists, in FModel's order and with its labels.</summary>
    public static readonly (string Section, string Label, string Name)[] Entries =
    [
        ("NAVIGATION", "Left Switch on Directory Tab", nameof(DirLeftTab)),
        ("NAVIGATION", "Right Switch on Directory Tab", nameof(DirRightTab)),
        ("NAVIGATION", "Left Switch on Asset Tab", nameof(AssetLeftTab)),
        ("NAVIGATION", "Right Switch on Asset Tab", nameof(AssetRightTab)),
        ("NAVIGATION", "Add Asset Tab", nameof(AssetAddTab)),
        ("NAVIGATION", "Remove Selected Asset Tab", nameof(AssetRemoveTab)),
        ("NAVIGATION", "Switch Asset Explorer", nameof(SwitchAssetExplorer)),
        ("EXPORT", "Export Raw Data", nameof(ExportData)),
        ("EXPORT", "Export Properties", nameof(ExportProperties)),
        ("EXPORT", "Export Textures", nameof(ExportTextures)),
        ("EXPORT", "Export Models", nameof(ExportModels)),
        ("EXPORT", "Export Worlds", nameof(ExportWorlds)),
        ("EXPORT", "Export Animations", nameof(ExportAnimations)),
        ("EXPORT", "Export Audio", nameof(ExportAudio)),
        ("EXPORT", "Export Decompiled Blueprints", nameof(ExportCode)),
        ("AUDIO PLAYER", "Add Audio File", nameof(AddAudio)),
        ("AUDIO PLAYER", "Play / Pause Current Audio", nameof(PlayPauseAudio)),
        ("AUDIO PLAYER", "Previous Audio", nameof(PreviousAudio)),
        ("AUDIO PLAYER", "Next Audio", nameof(NextAudio)),
        ("AUDIO PLAYER", "Remove Selected Audio", nameof(RemoveAudio))
    ];

    public Hotkey Get(string name) => (Hotkey?) typeof(HotkeySettings).GetProperty(name)?.GetValue(this) ?? Hotkey.None;

    public void Set(string name, Hotkey hotkey) => typeof(HotkeySettings).GetProperty(name)?.SetValue(this, hotkey);

    public HotkeySettings Clone() => (HotkeySettings) MemberwiseClone(); // Hotkey is immutable
}
