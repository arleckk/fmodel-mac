using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace FModel.Controls;

// FModel draws icons as <Viewbox 16x16><Canvas 24x24><Path/></Canvas></Viewbox>, which keeps the
// 24px grid's padding (PathIcon would stretch the geometry bounds instead).
public sealed class Glyph : Viewbox
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Glyph, Geometry?>(nameof(Data));

    // Inherits the surrounding text color (menu items, status bar…), like FModel binding to ForegroundBrush
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Glyph>();

    private readonly Avalonia.Controls.Shapes.Path _path = new();

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Glyph()
    {
        Width = 16;
        Height = 16;
        Child = new Canvas { Width = 24, Height = 24, Children = { _path } };
        _path.Fill = Foreground;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty) _path.Data = Data;
        else if (change.Property == ForegroundProperty) _path.Fill = Foreground;
    }
}
