using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views;

// Views › Image Merger (FModel's ImageMerger)
public partial class ImageMergerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _redraw;
    private byte[]? _png;
    private int _version;
    private bool _autoPerRow = true;

    // Designer only
    public ImageMergerWindow() : this(new MainViewModel()) { }

    public ImageMergerWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        ImagesList.ItemsSource = vm.MergerImages;
        MarginSlider.Value = Math.Clamp(vm.Settings.ImageMergerMargin, 0, 50);
        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, async (_, _) =>
        {
            _redraw!.Stop();
            await RedrawAsync();
        });
        vm.MergerImages.CollectionChanged += OnImagesChanged;
        Closed += (_, _) =>
        {
            vm.MergerImages.CollectionChanged -= OnImagesChanged;
            vm.SaveSettings(); // the margin, like FModel's ImageMergerMargin
        };
        SuggestPerRow();
        UpdateButtons();
        Redraw();
    }

    // FModel: images per row becomes the square root of the count whenever images are added
    private void SuggestPerRow()
    {
        var count = _vm.MergerImages.Count;
        _autoPerRow = true;
        PerRowSlider.Value = Math.Max(1, Math.Min(count, Math.Round(Math.Sqrt(count))));
        _autoPerRow = false;
    }

    private void OnImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) SuggestPerRow();
        UpdateButtons();
        Redraw();
    }

    private void OnLayoutChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        // Raised while the XAML loads, before the labels exist
        if (PerRowText is null || MarginText is null) return;
        PerRowText.Text = $"{(int) PerRowSlider.Value}";
        MarginText.Text = $"{(int) MarginSlider.Value}";
        if (sender == MarginSlider) _vm.Settings.ImageMergerMargin = (int) MarginSlider.Value;
        if (!_autoPerRow) Redraw();
    }

    private void Redraw()
    {
        _redraw.Stop();
        _redraw.Start();
    }

    private async Task RedrawAsync()
    {
        var images = _vm.MergerImages.ToArray();
        var version = ++_version;
        if (images.Length == 0)
        {
            SetPreview(null, null);
            return;
        }
        int perRow = (int) PerRowSlider.Value, margin = (int) MarginSlider.Value;
        ResultText.Text = "Merging…";
        try
        {
            var (png, width, height) = await Task.Run(() => ImageMerger.Merge(images, perRow, margin));
            if (version != _version) return; // a newer layout is on its way
            var bitmap = await Task.Run(() => new Bitmap(new MemoryStream(png)));
            SetPreview(png, bitmap);
            ResultText.Text = $"{width}×{height} · {images.Length} image(s) · {AssetPreview.FormatSize(png.Length)}";
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            SetPreview(null, null);
            ResultText.Text = ex.Message;
        }
    }

    private void SetPreview(byte[]? png, Bitmap? bitmap)
    {
        var old = Preview.Source as Bitmap;
        Preview.Source = bitmap;
        old?.Dispose();
        _png = png;
        EmptyText.IsVisible = bitmap is null;
        if (bitmap is null) ResultText.Text = "";
        OpenButton.IsEnabled = SaveButton.IsEnabled = png is not null;
    }

    private int[] SelectedIndices => ImagesList.SelectedItems?.OfType<MergeImage>()
        .Select(i => _vm.MergerImages.IndexOf(i)).Where(i => i >= 0).Order().ToArray() ?? [];

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var selected = SelectedIndices;
        UpButton.IsEnabled = selected.Length > 0 && selected[0] > 0;
        DownButton.IsEnabled = selected.Length > 0 && selected[^1] < _vm.MergerImages.Count - 1;
        RemoveButton.IsEnabled = selected.Length > 0;
        ClearButton.IsEnabled = _vm.MergerImages.Count > 0;
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var exports = _vm.Settings.ExportsDir;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add image(s)",
            AllowMultiple = true,
            SuggestedStartLocation = Directory.Exists(exports) ? await StorageProvider.TryGetFolderFromPathAsync(exports) : null,
            FileTypeFilter = [new FilePickerFileType("Image Files") { Patterns = ImageMerger.Patterns }, FilePickerFileTypes.All]
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        var loaded = await Task.Run(() => paths.Select(p => (Path: p, Image: ImageMerger.Load(p))).ToArray());
        foreach (var (path, image) in loaded)
        {
            if (image is null) _vm.Log(LogKind.Warning, $"Image Merger: '{Path.GetFileName(path)}' isn't an image it can read");
            else _vm.MergerImages.Add(image);
        }
    }

    // Moves the selection one step, keeping it selected (FModel's Up/Down buttons)
    private void Move(int step)
    {
        var images = _vm.MergerImages;
        var selected = SelectedIndices;
        var items = selected.Select(i => images[i]).ToArray();
        foreach (var index in step < 0 ? selected : selected.Reverse())
            images.Move(index, index + step);
        foreach (var item in items) ImagesList.SelectedItems?.Add(item);
        UpdateButtons();
    }

    private void OnUpClick(object? sender, RoutedEventArgs e) => Move(-1);

    private void OnDownClick(object? sender, RoutedEventArgs e) => Move(1);

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        foreach (var index in SelectedIndices.Reverse()) _vm.MergerImages.RemoveAt(index);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => _vm.MergerImages.Clear();

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_png is null) return;
        var path = Path.Combine(Path.GetTempPath(), "Merged Image.png");
        File.WriteAllBytes(path, _png);
        Process.Start(new ProcessStartInfo("open", [path]) { UseShellExecute = false });
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_png is not { } png) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image",
            SuggestedFileName = "Merged.png",
            DefaultExtension = "png",
            SuggestedStartLocation = Directory.Exists(_vm.Settings.OutputDir) ? await StorageProvider.TryGetFolderFromPathAsync(_vm.Settings.OutputDir) : null,
            FileTypeChoices = [FilePickerFileTypes.ImagePng]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(png);
        _vm.Log(LogKind.Information, $"Successfully saved '{file.Name}'");
    }
}
