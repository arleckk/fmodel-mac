using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace FModelMac.Services;

public sealed record BackupEntry(string Path, long Size, bool IsEncrypted);

/// <summary>What changed in the mounted game since a backup.</summary>
public sealed record BackupComparison(int BackupCount, HashSet<string> New, HashSet<string> Modified, int Removed);

/// <summary>A .fbkp file in the backups folder.</summary>
public sealed record BackupFile(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public long Size => new FileInfo(Path).Length;
    public DateTime Modified => File.GetLastWriteTime(Path);
    public string Details => $"{AssetPreview.FormatSize(Size)} · {Modified:yyyy-MM-dd HH:mm}";
}

/// <summary>
/// FModel's backups (.fbkp): the path, size and encryption of every file of a game at one point in time, compressed
/// as an LZ4 frame. Loading "All (New)" or "All (Modified)" against an older backup shows only what changed.
/// Same format as FModel, so its backups (and the ones it downloads) work here and the other way around.
/// </summary>
public static class GameBackup
{
    public const uint Magic = 0x504B4246; // "FBKP"
    private const uint Lz4FrameMagic = 0x184D2204;

    private enum Version : byte
    {
        BeforeVersionWasAdded = 0,
        Initial,
        PerfectPath, // no more leading slash and ToLower
        Latest = PerfectPath
    }

    public static string Directory(AppSettings settings) => System.IO.Path.Combine(settings.OutputDir, "Backups");

    public static IReadOnlyList<BackupFile> List(AppSettings settings)
    {
        var directory = Directory(settings);
        return System.IO.Directory.Exists(directory)
            ? System.IO.Directory.EnumerateFiles(directory, "*.fbkp").Select(p => new BackupFile(p))
                .OrderByDescending(b => b.Modified).ToArray()
            : [];
    }

    /// <summary>FModel's CreateBackup: "{game}_{MM_dd_yyyy}.fbkp" in Output/Backups.</summary>
    public static (string Path, int Count) Create(IFileProvider provider, string gameName, string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"{gameName}_{DateTime.Now:MM'_'dd'_'yyyy}.fbkp");
        var entries = Files(provider).Select(f => new BackupEntry(f.Path, f.Size, f.IsEncrypted)).ToArray();
        Write(entries, path);
        return (path, entries.Length);
    }

    public static void Write(IReadOnlyCollection<BackupEntry> entries, string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var compressed = LZ4Stream.Encode(stream, LZ4Level.L00_FAST);
        using var writer = new BinaryWriter(compressed, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write((byte) Version.Latest);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Size);
            writer.Write(entry.IsEncrypted);
            writer.Write(entry.Path);
        }
    }

    /// <summary>How many files a backup of this game holds.</summary>
    public static int FileCount(IFileProvider provider) => Files(provider).Count();

    // The files FModel lists: no .uexp/.ubulk payloads, one entry per path (the one the provider resolves to)
    private static IEnumerable<GameFile> Files(IFileProvider provider)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return provider.Files.Values.Where(f => !f.IsUePackagePayload && seen.Add(f.Path));
    }

    public static List<BackupEntry> Read(string path) => ReadEntries(path).ToList();

    /// <summary>
    /// Streams the entries: FModel's backups of big games hold millions of files (Fortnite's ~8.5M), too many to
    /// keep in memory just to compare.
    /// </summary>
    public static IEnumerable<BackupEntry> ReadEntries(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var head = new byte[4];
        file.ReadExactly(head);
        file.Position = 0;
        using var data = BitConverter.ToUInt32(head) == Lz4FrameMagic
            ? (Stream) new BufferedStream(LZ4Stream.Decode(file, leaveOpen: true), 1 << 16)
            : new BufferedStream(file, 1 << 16);
        using var reader = new BinaryReader(data, Encoding.UTF8);

        if (reader.ReadUInt32() != Magic)
        {
            // FModel's first backups: fixed-size records around the path, which starts with '/'; the 4 bytes just
            // read belong to the first record
            var skip = 12;
            while (reader.ReadBytes(skip).Length == skip)
            {
                var size = reader.ReadInt64();
                var encrypted = reader.ReadBoolean();
                reader.ReadBytes(4);
                var name = reader.ReadString();
                reader.ReadBytes(4);
                yield return new BackupEntry(name.TrimStart('/'), size, encrypted);
                skip = 16;
            }
            yield break;
        }

        var version = (Version) reader.ReadByte();
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var size = reader.ReadInt64();
            var encrypted = reader.ReadBoolean();
            var name = reader.ReadString();
            if (version < Version.PerfectPath) name = name.TrimStart('/');
            yield return new BackupEntry(name, size, encrypted);
        }
    }

    /// <summary>
    /// FModel's "All (New)" (files the backup doesn't have) and "All (Modified)" (files whose size or encryption
    /// changed), plus how many of the backup's files are gone. Only the mounted game's files are kept in memory.
    /// </summary>
    public static BackupComparison Compare(IFileProvider provider, string backupPath) => Compare(provider, ReadEntries(backupPath));

    public static BackupComparison Compare(IFileProvider provider, IEnumerable<BackupEntry> backup)
    {
        var current = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(provider)) current[file.Path] = file;

        var inBackup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0, removed = 0;
        foreach (var entry in backup)
        {
            total++;
            if (!current.TryGetValue(entry.Path, out var file))
            {
                removed++;
                continue;
            }
            inBackup.Add(file.Path);
            if (entry.Size != file.Size || entry.IsEncrypted != file.IsEncrypted) modified.Add(file.Path);
        }
        var added = current.Keys.Where(k => !inBackup.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new BackupComparison(total, added, modified, removed);
    }
}
