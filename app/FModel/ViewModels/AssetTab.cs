using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;

namespace FModel.ViewModels;

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
    [ObservableProperty] private int _imageIndex;

    private IReadOnlyList<PreviewImage> _images = [];

    /// <summary>The package's images (FModel's tab images); <see cref="Image"/> is the one shown.</summary>
    public IReadOnlyList<PreviewImage> Images => _images;
    public PreviewImage? CurrentImage => ImageIndex < _images.Count ? _images[ImageIndex] : null;
    public byte[]? ImagePng => CurrentImage?.Png;
    public bool HasImage => Image is not null;
    public bool HasMultipleImages => _images.Count > 1;
    public string ImagePage => $"{ImageIndex + 1} / {_images.Count}";
    /// <summary>Pixel art is scaled without smoothing, like FModel's RenderNearestNeighbor.</summary>
    public BitmapInterpolationMode ImageInterpolation => CurrentImage?.NearestNeighbor == true ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
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
        SetImages([], null);
    }

    /// <param name="first">The first image, already decoded off the UI thread; the others decode when shown.</param>
    public void Apply(PreviewResult result, Bitmap? first)
    {
        Highlighting = AppResources.Highlighting(result.Highlight);
        Document = new TextDocument(result.Text);
        SetImages(result.Images ?? [], first);
    }

    public void ApplyText(string text, string highlight)
    {
        Highlighting = AppResources.Highlighting(highlight);
        Document = new TextDocument(text);
    }

    /// <summary>FModel's GoNextImage / GoPreviousImage: step through the images, wrapping around.</summary>
    public void StepImage(int step)
    {
        if (_images.Count < 2) return;
        ShowImage(((ImageIndex + step) % _images.Count + _images.Count) % _images.Count, null);
    }

    private void SetImages(IReadOnlyList<PreviewImage> images, Bitmap? first)
    {
        _images = images;
        OnPropertyChanged(nameof(Images));
        OnPropertyChanged(nameof(HasMultipleImages));
        ShowImage(0, first);
    }

    private void ShowImage(int index, Bitmap? decoded)
    {
        var old = Image;
        ImageIndex = index;
        var current = CurrentImage;
        try
        {
            Image = decoded ?? (current is null ? null : new Bitmap(new MemoryStream(current.Png)));
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not show image {Name}", current?.Name);
            Image = null;
        }
        ImageName = current?.Name;
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(ImagePng));
        OnPropertyChanged(nameof(ImagePage));
        OnPropertyChanged(nameof(ImageInterpolation));
        if (!ReferenceEquals(old, Image)) old?.Dispose();
    }
}
