using Avalonia;
using Avalonia.Controls;

namespace FModelMac.Controls;

/// <summary>
/// Lays tab headers in one row across the whole width: each gets the width its text needs plus an equal share of
/// what's left (equal tabs when the labels are alike, a long label like "Favorite Directories" isn't cut); when even
/// the labels don't fit, they shrink in proportion.
/// </summary>
public sealed class FillTabPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToArray();
        var needed = visible.Sum(c => c.DesiredSize.Width);
        var extra = visible.Length == 0 ? 0 : (finalSize.Width - needed) / visible.Length;
        var scale = needed > finalSize.Width && needed > 0 ? finalSize.Width / needed : 1;
        double x = 0;
        foreach (var child in visible)
        {
            var width = scale < 1 ? child.DesiredSize.Width * scale : child.DesiredSize.Width + extra;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
