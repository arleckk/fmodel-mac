using System.Text;
using CUE4Parse.UE4.Lua.unluac;

namespace FModel.Services;

/// <summary>FModel's Settings › Unluac: what unluac does with a compiled Lua chunk.</summary>
public sealed record LuaOptions(EUnluacFlags Flags, string? OpCodeMap)
{
    public bool Disassemble => Flags.HasFlag(EUnluacFlags.Disassemble);

    public static LuaOptions From(AppSettings settings)
    {
        var flags = settings.LuaMode == LuaMode.Disassemble ? EUnluacFlags.Disassemble : EUnluacFlags.Decompile;
        if (settings.LuaRawString) flags |= EUnluacFlags.RawString;
        if (settings.LuaNoDebug) flags |= EUnluacFlags.NoDebug;
        if (settings.LuaLuaj) flags |= EUnluacFlags.Luaj;
        var map = settings.CurrentLuaOpCodeMap;
        return new LuaOptions(flags, string.IsNullOrWhiteSpace(map) ? null : map);
    }
}

public enum LuaMode { Decompile, Disassemble }

/// <summary>What unluac made of a chunk: the text, or why there is none.</summary>
public sealed record LuaResult(string? Text, string? Error, bool Partial);

/// <summary>
/// FModel's "Decompile Lua": compiled Lua 5.0–5.5 chunks (.luac/.lua starting with "\x1bLua") through unluac. FModel
/// downloads a Windows/Linux build; FModel bundles libunluac.dylib, built from the same sources with GraalVM
/// Native Image (packaging/build-unluac.sh).
/// </summary>
public static class LuaDecompiler
{
    public const string LibraryName = "libunluac.dylib";
    private static readonly Lazy<Unluac?> Library = new(Load);

    /// <summary>Why the library couldn't be loaded, if it couldn't.</summary>
    public static string? LoadError { get; private set; }

    public static bool IsAvailable => Library.Value is not null;

    public static bool IsChunk(byte[] data) => data.Length >= 4 && BitConverter.ToUInt32(data) == UnluacHelper.LuaMagic;

    private static Unluac? Load()
    {
        var path = new[] { AppContext.BaseDirectory, AppSettings.DataDirectory }
            .Select(dir => Path.Combine(dir, LibraryName)).FirstOrDefault(File.Exists);
        if (path is null)
        {
            LoadError = $"{LibraryName} is missing (run packaging/build-unluac.sh)";
            return null;
        }
        try
        {
            return new Unluac(path);
        }
        catch (Exception e)
        {
            LoadError = $"Could not load {LibraryName}: {e.Message}";
            Serilog.Log.Warning(e, "Could not load {Path}", path);
            return null;
        }
    }

    public static LuaResult Decompile(byte[] chunk, LuaOptions options)
    {
        if (Library.Value is not { } unluac) return new LuaResult(null, LoadError, false);

        var flags = options.Flags;
        var map = Array.Empty<byte>();
        if (options.OpCodeMap is { } text)
        {
            map = Encoding.UTF8.GetBytes(text);
            flags |= EUnluacFlags.OpCodeMap;
        }
        // Each call runs in its own GraalVM isolate, so calls from several threads don't interfere
        var code = unluac.Decompile(chunk, map, (uint) flags, out var output, out var log);
        var logText = log.Length > 0 ? Encoding.UTF8.GetString(log).Trim() : null;
        var outputText = output.Length > 0 ? Encoding.UTF8.GetString(output) : null;
        return code switch
        {
            EUnluacErrorCode.Ok when outputText is not null => new LuaResult(outputText, null, false),
            EUnluacErrorCode.PartialDecompile when outputText is not null => new LuaResult(outputText, FirstLine(logText) ?? "partial decompile", true),
            EUnluacErrorCode.CorruptedOpCodeMap => new LuaResult(null, "The opcode map is invalid: " + (FirstLine(logText) ?? "unluac rejected it"), false),
            _ => new LuaResult(null, FirstLine(logText) ?? "unluac couldn't decompile it", false)
        };
    }

    private static string? FirstLine(string? text) => text?.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : null;
}
