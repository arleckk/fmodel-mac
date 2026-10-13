namespace FModelMac.Services.Audio;

/// <summary>Saves sounds under Exports, mirroring their game path, as WAV or in their original format.</summary>
public static class AudioExport
{
    public static string Save(ExtractedSound sound, AppSettings settings, PcmAudio? decoded = null)
    {
        var basePath = settings.ExportPath(sound.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

        if (!settings.ConvertAudioToWav || sound.Extension == "wav" && LooksLikeWav(sound.Data))
        {
            var raw = $"{basePath}.{sound.Extension}";
            File.WriteAllBytes(raw, sound.Data);
            return raw;
        }

        decoded ??= AudioDecoder.Decode(sound.Data, sound.Extension, settings.CriwareKeyValue);
        var wav = $"{basePath}.wav";
        File.WriteAllBytes(wav, AudioDecoder.ToWav(decoded));
        return wav;
    }

    private static bool LooksLikeWav(byte[] data) => data is [(byte) 'R', (byte) 'I', (byte) 'F', (byte) 'F', ..];
}
