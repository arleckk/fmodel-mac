using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.FileProvider.Objects;
using FModelMac.Services;

namespace FModelMac.ViewModels;

// One tab of FModel's right-hand GameFilesTabControl: properties text plus an optional image preview
public sealed partial class AssetTab : ObservableObject
{
    public const string NewTabHeader = "New Tab";

    [ObservableProperty] private string _header = NewTabHeader;
    [ObservableProperty] private GameFile? _entry;
    [ObservableProperty] private TextDocument _document = new();
    [ObservableProperty] private IHighlightingDefinition? _highlighting;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private string? _imageName;

    public byte[]? ImagePng { get; private set; }
    public bool HasImage => Image is not null;
    public CancellationTokenSource? Loading { get; set; }

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    // FModel's SoftReset: reuse the tab for another entry
    public void Reset(GameFile? entry)
    {
        Loading?.Cancel();
        Entry = entry;
        Header = entry?.Name ?? NewTabHeader;
        Document = new TextDocument();
        Highlighting = null;
        SetImage(null, null);
    }

    public void Apply(PreviewResult result, Bitmap? image)
    {
        Highlighting = AppResources.Highlighting(result.Highlight);
        Document = new TextDocument(result.Text);
        ImagePng = result.Image;
        SetImage(image, result.ImageName);
    }

    private void SetImage(Bitmap? image, string? name)
    {
        var old = Image;
        Image = image;
        ImageName = name;
        if (image is null) ImagePng = null;
        if (!ReferenceEquals(old, image)) old?.Dispose();
    }
}
