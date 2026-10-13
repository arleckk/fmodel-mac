using Avalonia.Media;
using Avalonia.Media.Imaging;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModelMac.Services;

namespace FModelMac.ViewModels;

// Mirrors FModel's EStatusKind; drives the status bar color
public enum StatusKind
{
    Ready,
    Loading,
    Completed,
    Failed,
    Configuring
}

public enum LogKind
{
    Information,
    Warning,
    Error,
    Debug
}

// One console line, colored like FModel's FLogger ("[INF] " in blue, message in white…)
public sealed record LogEntry(LogKind Kind, string Message)
{
    private static readonly IBrush Blue = Brush.Parse("#528BCC");
    private static readonly IBrush Yellow = Brush.Parse("#E5C07B");
    private static readonly IBrush Red = Brush.Parse("#E06C75");
    private static readonly IBrush Green = Brush.Parse("#98C379");
    private static readonly IBrush White = Brush.Parse("#DAE5F2");

    public string Prefix => Kind switch
    {
        LogKind.Warning => "[WRN] ",
        LogKind.Error => "[ERR] ",
        LogKind.Debug => "[DBG] ",
        _ => "[INF] "
    };

    public IBrush PrefixBrush => Kind switch
    {
        LogKind.Warning => Yellow,
        LogKind.Error => Red,
        LogKind.Debug => Green,
        _ => Blue
    };

    public IBrush MessageBrush => Kind == LogKind.Error ? Red : White;
}

// Row of the "Archives" tab
public sealed class ArchiveItem(ArchiveEntry entry)
{
    public ArchiveEntry Entry { get; } = entry;
    public IAesVfsReader Reader => Entry.Reader;
    public string Name => Entry.Name;
    public bool IsEnabled => Entry.IsMounted;
    public string FileCountText => $"{Entry.FileCount:N0} files";
    public int FileCount => Entry.FileCount;
    public string LengthText => AssetPreview.FormatSize(Entry.Length);
    public string MountPoint => Entry.MountPoint;
    public bool IsEncrypted => Entry.IsEncrypted;
    public string Guid => Entry.Guid.ToString();
    public Bitmap? Icon => AppResources.Icon(IsEnabled ? "archive_enabled" : "archive_disabled");
}

// Row of the "Packages" tab
public sealed class AssetItem(GameFile file)
{
    public GameFile File { get; } = file;
    public string Name => File.Name;
    public Bitmap? Icon => AppResources.FileIcon(File.Extension);
    public string OffsetText => File is VfsEntry entry ? $"0x{entry.Offset:X}" : "0x0";
    public string SizeText => AssetPreview.FormatSize(File.Size);
    public string CompressionText => File.CompressionMethod.ToString();
    public bool IsEncrypted => File.IsEncrypted;
    public bool IsPackage => File.IsUePackage;
    public string ArchiveText => File is VfsEntry entry ? entry.Vfs.Name : "None";
}
