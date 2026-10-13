using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FModel.Controls;

// FModel's "CustomSeparator": a horizontal rule with a small bold caption in the middle (e.g. INFORMATION)
public sealed class SectionSeparator : Grid
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SectionSeparator, string?>(nameof(Text));

    private readonly TextBlock _caption = new()
    {
        FontWeight = FontWeight.SemiBold,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center
    };

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public SectionSeparator()
    {
        Margin = new Thickness(0, 8);
        ColumnDefinitions = new ColumnDefinitions("*,Auto,*");
        Children.Add(Rule(0));
        Children.Add(_caption);
        SetColumn(_caption, 1);
        Children.Add(Rule(2));
    }

    private Border Rule(int column)
    {
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("Layer1BorderBrush"));
        SetColumn(rule, column);
        return rule;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;

        _caption.Text = Text;
        _caption.Margin = string.IsNullOrEmpty(Text) ? default : new Thickness(10, 0);
    }
}
