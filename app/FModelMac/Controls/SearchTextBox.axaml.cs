using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace FModelMac.Controls;

public partial class SearchTextBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SearchTextBox, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<SearchTextBox, string?>(nameof(Watermark), "Search by name...");

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public SearchTextBox() => InitializeComponent();

    public void FocusInput() => Input.Focus();

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Text = string.Empty;
        Input.Focus();
    }
}
