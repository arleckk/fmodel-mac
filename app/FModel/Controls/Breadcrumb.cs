using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace FModel.Controls;

// FModel's Breadcrumb: one clickable segment per folder of the current path, separated by chevrons
public sealed class Breadcrumb : StackPanel
{
    private const string NavigateNext = "M9.31 6.71c-.39.39-.39 1.02 0 1.41L13.19 12l-3.88 3.88c-.39.39-.39 1.02 0 1.41.39.39 1.02.39 1.41 0l4.59-4.59c.39-.39.39-1.02 0-1.41L10.72 6.7c-.38-.38-1.02-.38-1.41.01z";

    private static readonly IBrush HoverBorder = new SolidColorBrush(Color.FromRgb(127, 127, 144));
    private static readonly IBrush HoverBackground = new SolidColorBrush(Color.FromRgb(72, 73, 92));

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<Breadcrumb, string?>(nameof(Path));

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    /// <summary>Raised with the full path of the clicked segment.</summary>
    public event EventHandler<string>? SegmentClicked;

    public Breadcrumb()
    {
        Orientation = Orientation.Horizontal;
        Height = 24;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (string.IsNullOrEmpty(Path)) return;

        var folders = Path.Split('/');
        for (var i = 0; i < folders.Length; i++)
        {
            var isLast = i == folders.Length - 1;
            var target = string.Join('/', folders, 0, i + 1);
            var segment = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 3),
                Cursor = isLast ? Cursor.Default : new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = folders[i], VerticalAlignment = VerticalAlignment.Center }
            };

            if (!isLast)
            {
                segment.PointerEntered += (_, _) => { segment.BorderBrush = HoverBorder; segment.Background = HoverBackground; };
                segment.PointerExited += (_, _) => { segment.BorderBrush = Brushes.Transparent; segment.Background = Brushes.Transparent; };
                segment.PointerReleased += (_, _) => SegmentClicked?.Invoke(this, target);
            }

            Children.Add(segment);
            if (isLast) continue;

            Children.Add(new Glyph
            {
                Data = StreamGeometry.Parse(NavigateNext),
                Foreground = Brushes.White,
                Opacity = 0.6
            });
        }
    }
}
