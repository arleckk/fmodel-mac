using System.Diagnostics;

namespace FModel.Services.Audio;

/// <summary>
/// vgmstream-cli decodes the game audio codecs .NET has no decoder for (Wwise .wem, Bink Audio, UE Opus, AT9…).
/// FModel downloads the Windows build on startup; the macOS release is linked against specific Homebrew library
/// versions, so the app ships its own build (packaging/build-vgmstream.sh: static, decoder-only dependencies).
/// A vgmstream-cli in the data folder or from Homebrew (brew install vgmstream) is used when it's missing.
/// </summary>
public static class Vgmstream
{
    public const string InstallCommand = "brew install vgmstream";

    private static string DataDirectory => Path.Combine(AppSettings.DataDirectory, "vgmstream");

    /// <summary>The copy inside the app bundle, next to the executable.</summary>
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "vgmstream-cli");

    public static bool IsBundled(string path) => path == BundledPath;

    /// <summary>The vgmstream-cli to use, or null when there is none.</summary>
    public static string? Locate()
    {
        if (File.Exists(BundledPath)) return BundledPath;
        if (Directory.Exists(DataDirectory) &&
            Directory.EnumerateFiles(DataDirectory, "vgmstream-cli", SearchOption.AllDirectories).FirstOrDefault() is { } own)
            return own;
        // Apps started from Finder don't get the shell's PATH: look where Homebrew installs too
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        return new[] { "/opt/homebrew/bin", "/usr/local/bin" }.Concat(path)
            .Select(dir => Path.Combine(dir, "vgmstream-cli")).FirstOrDefault(File.Exists);
    }

    public static bool IsAvailable => Locate() is not null;

    /// <summary>The vgmstream version (e.g. r2117), or null if it doesn't run.</summary>
    public static async Task<string?> VersionAsync(string? cli = null, CancellationToken ct = default)
    {
        cli ??= Locate();
        if (cli is null) return null;
        try
        {
            // Recent builds print JSON ({"version":"r2117",…}) and exit with 1, older ones a text line
            var (_, output) = await RunAsync(cli, ["-V"], ct);
            var text = output.Trim();
            if (text.Length == 0) return null;
            if (text.StartsWith('{'))
                return Newtonsoft.Json.Linq.JObject.Parse(text)["version"]?.ToString();
            return text.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "vgmstream at {Path} doesn't run", cli);
            return null;
        }
    }

    /// <summary>Converts a sound to WAV. The extension tells vgmstream how to read the data (wem, binka, opus…).</summary>
    public static byte[] ToWav(byte[] data, string extension)
    {
        var cli = Locate() ?? throw new VgmstreamMissingException(extension);
        var work = Directory.CreateTempSubdirectory("fmodel-audio");
        try
        {
            var input = Path.Combine(work.FullName, $"sound.{extension.ToLowerInvariant()}");
            var output = Path.Combine(work.FullName, "sound.wav");
            File.WriteAllBytes(input, data);
            var (code, log) = RunAsync(cli, ["-o", output, input], CancellationToken.None, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (code != 0 || !File.Exists(output))
                throw new InvalidDataException($"vgmstream couldn't decode this {extension.ToUpperInvariant()} sound" +
                                               (string.IsNullOrWhiteSpace(log) ? "" : $": {log.Trim().Split('\n').Last()}"));
            return File.ReadAllBytes(output);
        }
        finally
        {
            work.Delete(true);
        }
    }

    /// <summary>
    /// UE 5 RAD Audio isn't in vgmstream: like FModel, it needs RAD's own decoder, built by the user from the
    /// Unreal Engine sources and placed in the app's data folder as "radadec" (FModel uses radadec.exe).
    /// </summary>
    public static string RadaDecoderPath => Path.Combine(AppSettings.DataDirectory, "radadec");

    public const string RadaHelpLink = "https://github.com/4sval/FModel/discussions/422";

    public static byte[] RadaToWav(byte[] data)
    {
        if (!File.Exists(RadaDecoderPath))
            throw new MissingDecoderException($"RAD Audio needs RAD's decoder: build radadec from Unreal Engine (see {RadaHelpLink}) " +
                                           $"and put it at {RadaDecoderPath}");
        var work = Directory.CreateTempSubdirectory("fmodel-audio");
        try
        {
            var input = Path.Combine(work.FullName, "sound.rada");
            var output = Path.Combine(work.FullName, "sound.wav");
            File.WriteAllBytes(input, data);
            var (code, log) = RunAsync(RadaDecoderPath, ["-i", input, "-o", output], CancellationToken.None, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (code != 0 || !File.Exists(output))
                throw new InvalidDataException($"radadec couldn't decode this sound{(string.IsNullOrWhiteSpace(log) ? "" : $": {log.Trim().Split('\n').Last()}")}");
            return File.ReadAllBytes(output);
        }
        finally
        {
            work.Delete(true);
        }
    }

    /// <summary>Every subsong of a sound bank vgmstream can read (Square Enix .sab), as named WAVs.</summary>
    public static List<(string Name, byte[] Wav)> ExtractSubsongs(byte[] data, string extension)
    {
        var cli = Locate() ?? throw new VgmstreamMissingException(extension);
        var work = Directory.CreateTempSubdirectory("fmodel-audio");
        try
        {
            var input = Path.Combine(work.FullName, $"bank.{extension}");
            File.WriteAllBytes(input, data);
            var outDir = Directory.CreateDirectory(Path.Combine(work.FullName, "out")).FullName;
            RunAsync(cli, ["-S", "0", "-o", Path.Combine(outDir, "?n_?s.wav"), input], CancellationToken.None, TimeSpan.FromMinutes(2))
                .GetAwaiter().GetResult();
            return Directory.EnumerateFiles(outDir, "*.wav").Order()
                .Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f))).ToList();
        }
        finally
        {
            work.Delete(true);
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string[] args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
