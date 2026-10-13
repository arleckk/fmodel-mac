using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FModelMac.Services;

namespace FModelMac.Controls;

/// <summary>
/// FModel's HotkeyTextBox: press a key combination to assign it; Delete, Backspace or Escape (without modifiers)
/// unassign it. Tab still moves to the next box.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly StyledProperty<Hotkey> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, Hotkey>(nameof(Hotkey), Hotkey.None);

    public Hotkey Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyBox()
    {
        IsReadOnly = true;
        Watermark = "None";
        Cursor = new Cursor(StandardCursorType.Hand);
        CaretBrush = Avalonia.Media.Brushes.Transparent;
        Text = "";
        // Before the TextBox's own key handling (copy, select all…) and the window's default/cancel buttons
        AddHandler(KeyDownEvent, OnCapture, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HotkeyProperty) Text = Hotkey.ToString();
    }

    private void OnCapture(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) return;
        e.Handled = true;
        if (e.Key is Key.Delete or Key.Back or Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Hotkey = Hotkey.None;
            return;
        }
        if (Hotkey.IsModifier(e.Key)) return;
        Hotkey = new Hotkey(e.Key, e.KeyModifiers);
    }
}
