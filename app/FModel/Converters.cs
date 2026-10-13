using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FModel.ViewModels;

namespace FModel;

public static class Converters
{
    private static readonly IBrush Accent = Brush.Parse("#206BD4");
    private static readonly IBrush Alert = Brush.Parse("#D49220");
    private static readonly IBrush Error = Brush.Parse("#C22B2B");

    // Same triggers as FModel's StatusBar style
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<StatusKind, IBrush>(kind => kind switch
    {
        StatusKind.Loading or StatusKind.Configuring => Alert,
        StatusKind.Failed => Error,
        _ => Accent
    });

    // FModel hides (rather than collapses) the folder expander when there are no sub folders
    public static readonly IValueConverter VisibleOpacity = new FuncValueConverter<bool, double>(visible => visible ? 1 : 0);

    // Favorite directories the mounted game doesn't have are dimmed
    public static readonly IValueConverter MissingOpacity = new FuncValueConverter<bool, double>(missing => missing ? 0.45 : 1);
    public static readonly IValueConverter FavoriteStatus = new FuncValueConverter<bool, string>(missing => missing ? "Not Found" : "Found");

    // The logger expander header gets a bottom margin once open
    public static readonly IValueConverter ExpanderHeaderMargin = new FuncValueConverter<bool, Avalonia.Thickness>(open =>
        open ? new Avalonia.Thickness(0, 0, 0, 5) : default);

    // Multiple/All modes allow picking several archives (FModel's DirectoryFilesListBox triggers)
    public static readonly IValueConverter SelectionMode = new FuncValueConverter<bool, SelectionMode>(multiple =>
        multiple ? Avalonia.Controls.SelectionMode.Multiple : Avalonia.Controls.SelectionMode.Single);
}
