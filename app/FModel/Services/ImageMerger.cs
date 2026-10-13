using System.Diagnostics;
using SkiaSharp;

namespace FModel.Services;

/// <summary>An image in the merger: its name in the list and its encoded bytes.</summary>
public sealed record MergeImage(string Name, byte[] Data, int Width, int Height)
{
    public string Details => $"{Width}×{Height}";

    public static MergeImage? FromBytes(string name, byte[] data) => ImageMerger.FromBytes(name, data);
}

/// <summary>FModel's Image Merger: images laid out in rows, left to right, with a margin between them.</summary>
public static class ImageMerger
{
    /// <summary>What the file picker offers. Skia decodes most of them; TIFF goes through macOS's sips.</summary>
    public static readonly string[] Patterns = ["*.png", "*.jpg", "*.jpeg", "*.jfif", "*.jpe", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff"];

    public static MergeImage? Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff") data = TiffToPng(path) ?? data;
        return FromBytes(Path.GetFileNameWithoutExtension(path), data);
    }

    public static MergeImage? FromBytes(string name, byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        return codec is null ? null : new MergeImage(name, data, codec.Info.Width, codec.Info.Height);
    }

    // FModel converts TIFF with System.Drawing (Windows only); macOS has sips
    private static byte[]? TiffToPng(string path)
    {
        var output = Path.Combine(Path.GetTempPath(), $"fmodel-{Guid.NewGuid():N}.png");
        try
        {
            using var sips = Process.Start(new ProcessStartInfo("/usr/bin/sips", ["-s", "format", "png", path, "--out", output])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            sips?.WaitForExit(30_000);
            return File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>FModel's DrawPreview layout: rows of <paramref name="perRow"/> images, each row as tall as its tallest.</summary>
    public static (byte[] Png, int Width, int Height) Merge(IReadOnlyList<MergeImage> images, int perRow, int margin)
    {
        if (images.Count == 0) throw new ArgumentException("No images to merge", nameof(images));
        perRow = Math.Max(1, perRow);
        margin = Math.Max(0, margin);

        var bitmaps = images.Select(i => SKBitmap.Decode(i.Data) ?? throw new InvalidDataException($"Can't decode '{i.Name}'")).ToArray();
        try
        {
            var positions = new SKPoint[bitmaps.Length];
            int x = 0, y = 0, width = 0, height = 0, rowHeight = 0;
            for (var i = 0; i < bitmaps.Length; i++)
            {
                var image = bitmaps[i];
                positions[i] = new SKPoint(x, y);
                rowHeight = Math.Max(rowHeight, image.Height);
                width = Math.Max(width, x + image.Width);
                height = Math.Max(height, y + rowHeight);
                if ((i + 1) % perRow == 0)
                {
                    x = 0;
                    y += rowHeight + margin;
                    rowHeight = 0;
                }
                else x += image.Width + margin;
            }

            using var merged = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(merged);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            for (var i = 0; i < bitmaps.Length; i++) canvas.DrawBitmap(bitmaps[i], positions[i], paint);
            using var data = merged.Encode(SKEncodedImageFormat.Png, 100);
            return (data.ToArray(), width, height);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }
}
