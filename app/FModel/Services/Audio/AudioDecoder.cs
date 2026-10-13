using System.Buffers.Binary;
using CUE4Parse.UE4.Criware.Decoders.ADX;
using CUE4Parse.UE4.Criware.Decoders.HCA;
using NVorbis;

namespace FModel.Services.Audio;

/// <summary>Decoded audio: interleaved 32-bit float samples.</summary>
public sealed record PcmAudio(float[] Samples, int Channels, int SampleRate, string Encoding)
{
    public int Frames => Samples.Length / Math.Max(1, Channels);
    public TimeSpan Duration => TimeSpan.FromSeconds((double) Frames / Math.Max(1, SampleRate));
}

/// <summary>
/// Turns the audio CUE4Parse extracts into PCM. WAV (PCM, float, MS and IMA ADPCM) and Ogg Vorbis are decoded
/// here, CRIWARE HCA/ADX by CUE4Parse; everything else (Wwise .wem, Bink, UE Opus, AT9, XVAG…) goes through
/// vgmstream, like FModel does on Windows.
/// </summary>
public static class AudioDecoder
{
    public static PcmAudio Decode(byte[] data, string format, ulong criwareKey = 0)
    {
        var ext = format.ToLowerInvariant();
        if (data.Length < 4) throw new InvalidDataException("The sound has no data");

        switch (ext)
        {
            case "hca":
                return Wav.Decode(HcaWaveStream.ConvertHcaToWav(data, criwareKey), "HCA");
            case "adx":
                var adx = AdxDecoder.ConvertAdxToWav(data, criwareKey);
                if (adx.Length > 0) return Wav.Decode(adx, "ADX");
                break;
            case "rada":
                return Wav.Decode(Vgmstream.RadaToWav(data), "RAD Audio");
        }

        if (IsOgg(data)) return DecodeVorbis(data);
        // Plain WAVs (and UE's PCM/ADPCM sound waves). Wwise .wem files are RIFF too, but with their own codecs.
        if (IsRiff(data) && ext != "wem" && Wav.IsSupported(data)) return Wav.Decode(data, null);
        if (ext == "wem" && Wav.IsSupported(data) && !Vgmstream.IsAvailable) return Wav.Decode(data, null);

        if (!Vgmstream.IsAvailable)
            throw new VgmstreamMissingException(format);
        try
        {
            return Wav.Decode(Vgmstream.ToWav(data, ext), ext.ToUpperInvariant());
        }
        catch (InvalidDataException) when (IsRiff(data) && Wav.IsSupported(data))
        {
            return Wav.Decode(data, null); // a plain WAV with a game extension
        }
    }

    /// <summary>A playable/shareable .wav of the sound (re-encoded from the decoded samples).</summary>
    public static byte[] ToWav(PcmAudio audio) => Wav.Encode(audio);

    private static bool IsOgg(byte[] data) => data is [(byte) 'O', (byte) 'g', (byte) 'g', (byte) 'S', ..];
    private static bool IsRiff(byte[] data) => data is [(byte) 'R', (byte) 'I', (byte) 'F', (byte) 'F', ..];

    private static PcmAudio DecodeVorbis(byte[] data)
    {
        using var reader = new VorbisReader(new MemoryStream(data), true);
        var channels = reader.Channels;
        var samples = new List<float>(reader.TotalSamples > 0 ? (int) Math.Min(reader.TotalSamples * channels, int.MaxValue / 2) : 1 << 16);
        var buffer = new float[channels * 4096];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return new PcmAudio(samples.ToArray(), channels, reader.SampleRate, "Vorbis");
    }
}

/// <summary>A sound that needs an external decoder the user hasn't installed (not an error in the data).</summary>
public class MissingDecoderException(string message) : Exception(message);

public sealed class VgmstreamMissingException(string format)
    : MissingDecoderException($"{format.ToUpperInvariant()} audio needs vgmstream: install it with '{Vgmstream.InstallCommand}' (see Settings › Audio)");

/// <summary>RIFF WAVE reading and writing.</summary>
internal static class Wav
{
    private const int TagPcm = 1, TagMsAdpcm = 2, TagFloat = 3, TagImaAdpcm = 0x11, TagExtensible = 0xFFFE;

    private sealed record Format(int Tag, int Channels, int SampleRate, int BlockAlign, int Bits, int SamplesPerBlock, short[] Coefficients);

    public static bool IsSupported(byte[] data) =>
        TryParse(data, out var format, out _) && format.Tag is TagPcm or TagFloat or TagMsAdpcm or TagImaAdpcm;

    public static PcmAudio Decode(byte[] data, string? encoding)
    {
        if (!TryParse(data, out var format, out var body))
            throw new InvalidDataException("Not a WAV file");
        var samples = format.Tag switch
        {
            TagPcm => DecodePcm(body, format.Bits),
            TagFloat when format.Bits == 32 => DecodeFloat(body),
            TagFloat when format.Bits == 64 => DecodeDouble(body),
            TagMsAdpcm => DecodeMsAdpcm(body, format),
            TagImaAdpcm => DecodeImaAdpcm(body, format),
            _ => throw new NotSupportedException($"WAV codec 0x{format.Tag:X4} isn't supported")
        };
        encoding ??= format.Tag switch
        {
            TagPcm => $"PCM {format.Bits}-bit",
            TagFloat => $"IEEE float {format.Bits}-bit",
            TagMsAdpcm => "MS ADPCM",
            TagImaAdpcm => "IMA ADPCM",
            _ => "WAV"
        };
        return new PcmAudio(samples, format.Channels, format.SampleRate, encoding);
    }

    private static bool TryParse(byte[] data, out Format format, out ReadOnlySpan<byte> body)
    {
        format = null!;
        body = default;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0x52494646 ||
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) != 0x57415645) // RIFF … WAVE
            return false;

        Format? fmt = null;
        var span = data.AsSpan();
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            var size = (int) Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]), (uint) (data.Length - offset - 8));
            var chunk = span.Slice(offset + 8, size);
            if (id == 0x666D7420 && size >= 16) // "fmt "
            {
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                var rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                int align = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]);
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                var extra = size >= 18 ? chunk[18..Math.Min(size, 18 + BinaryPrimitives.ReadUInt16LittleEndian(chunk[16..]))] : [];
                if (tag == TagExtensible && extra.Length >= 8) tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[6..]);

                var samplesPerBlock = extra.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(extra) : 0;
                short[] coefficients = [];
                if (tag == TagMsAdpcm)
                {
                    int count = extra.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]) : 0;
                    if (count > 0 && extra.Length >= 4 + count * 4)
                    {
                        coefficients = new short[count * 2];
                        for (var i = 0; i < coefficients.Length; i++) coefficients[i] = BinaryPrimitives.ReadInt16LittleEndian(extra[(4 + i * 2)..]);
                    }
                    else coefficients = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];
                    if (samplesPerBlock == 0 && channels > 0) samplesPerBlock = (align - 7 * channels) * 2 / channels + 2;
                }
                else if (tag == TagImaAdpcm && samplesPerBlock == 0 && channels > 0)
                {
                    samplesPerBlock = (align - 4 * channels) * 2 / channels + 1;
                }
                fmt = new Format(tag, channels, rate, align, bits, samplesPerBlock, coefficients);
            }
            else if (id == 0x64617461 && fmt is not null) // "data"
            {
                format = fmt;
                body = chunk;
                return fmt.Channels > 0 && fmt.SampleRate > 0;
            }
            offset += 8 + size + (size & 1);
        }
        return false;
    }

    private static float[] DecodePcm(ReadOnlySpan<byte> body, int bits)
    {
        var bytes = Math.Max(1, bits / 8);
        var samples = new float[body.Length / bytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = body.Slice(i * bytes, bytes);
            samples[i] = bytes switch
            {
                1 => (s[0] - 128) / 128f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f
            };
        }
        return samples;
    }

    private static float[] DecodeFloat(ReadOnlySpan<byte> body)
    {
        var samples = new float[body.Length / 4];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadSingleLittleEndian(body[(i * 4)..]);
        return samples;
    }

    private static float[] DecodeDouble(ReadOnlySpan<byte> body)
    {
        var samples = new float[body.Length / 8];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float) BinaryPrimitives.ReadDoubleLittleEndian(body[(i * 8)..]);
        return samples;
    }

    private static readonly int[] MsAdaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    private static float[] DecodeMsAdpcm(ReadOnlySpan<byte> body, Format f)
    {
        int ch = f.Channels, align = f.BlockAlign, perBlock = f.SamplesPerBlock;
        if (align <= 7 * ch || perBlock < 2) throw new InvalidDataException("Invalid MS ADPCM header");
        var blocks = (body.Length + align - 1) / align;
        var output = new List<float>(blocks * perBlock * ch);
        Span<int> coef1 = stackalloc int[ch], coef2 = stackalloc int[ch], delta = stackalloc int[ch], s1 = stackalloc int[ch], s2 = stackalloc int[ch];

        for (var b = 0; b < blocks; b++)
        {
            var block = body.Slice(b * align, Math.Min(align, body.Length - b * align));
            if (block.Length < 7 * ch) break;
            for (var c = 0; c < ch; c++)
            {
                var predictor = Math.Min(block[c], f.Coefficients.Length / 2 - 1);
                coef1[c] = f.Coefficients[predictor * 2];
                coef2[c] = f.Coefficients[predictor * 2 + 1];
                delta[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(ch + c * 2)..]);
                s1[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(3 * ch + c * 2)..]);
                s2[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(5 * ch + c * 2)..]);
            }
            for (var c = 0; c < ch; c++) output.Add(s2[c] / 32768f);
            for (var c = 0; c < ch; c++) output.Add(s1[c] / 32768f);

            // Nibbles, high first, channels interleaved nibble by nibble
            var nibbles = (block.Length - 7 * ch) * 2;
            var frames = Math.Min(perBlock - 2, nibbles / ch);
            for (var n = 0; n < frames * ch; n++)
            {
                var c = n % ch;
                var raw = block[7 * ch + n / 2];
                var nibble = (n & 1) == 0 ? raw >> 4 : raw & 0x0F;
                var signed = nibble >= 8 ? nibble - 16 : nibble;
                var predicted = ((s1[c] * coef1[c] + s2[c] * coef2[c]) >> 8) + signed * delta[c];
                predicted = Math.Clamp(predicted, short.MinValue, short.MaxValue);
                s2[c] = s1[c];
                s1[c] = predicted;
                delta[c] = Math.Max(16, (MsAdaptation[nibble] * delta[c]) >> 8);
                output.Add(predicted / 32768f);
            }
        }
        return output.ToArray();
    }

    private static readonly int[] ImaIndex = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] ImaSteps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ];

    private static float[] DecodeImaAdpcm(ReadOnlySpan<byte> body, Format f)
    {
        int ch = f.Channels, align = f.BlockAlign, perBlock = f.SamplesPerBlock;
        if (align <= 4 * ch || perBlock < 1) throw new InvalidDataException("Invalid IMA ADPCM header");
        var blocks = (body.Length + align - 1) / align;
        var output = new List<float>(blocks * perBlock * ch);
        Span<int> predictor = stackalloc int[ch], index = stackalloc int[ch];
        var frame = new float[ch * 8];

        for (var b = 0; b < blocks; b++)
        {
            var block = body.Slice(b * align, Math.Min(align, body.Length - b * align));
            if (block.Length < 4 * ch) break;
            for (var c = 0; c < ch; c++)
            {
                predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(c * 4)..]);
                index[c] = Math.Clamp((int) block[c * 4 + 2], 0, 88);
                output.Add(predictor[c] / 32768f);
            }

            // Groups of 4 bytes (8 samples, low nibble first) per channel, channels interleaved
            var data = block[(4 * ch)..];
            for (var group = 0; (group + 1) * 4 * ch <= data.Length && 1 + group * 8 < perBlock; group++)
            {
                for (var c = 0; c < ch; c++)
                for (var k = 0; k < 8; k++)
                {
                    var raw = data[group * 4 * ch + c * 4 + k / 2];
                    var nibble = (k & 1) == 0 ? raw & 0x0F : raw >> 4;
                    var step = ImaSteps[index[c]];
                    var diff = step >> 3;
                    if ((nibble & 4) != 0) diff += step;
                    if ((nibble & 2) != 0) diff += step >> 1;
                    if ((nibble & 1) != 0) diff += step >> 2;
                    predictor[c] = Math.Clamp(predictor[c] + ((nibble & 8) != 0 ? -diff : diff), short.MinValue, short.MaxValue);
                    index[c] = Math.Clamp(index[c] + ImaIndex[nibble], 0, 88);
                    frame[k * ch + c] = predictor[c] / 32768f;
                }
                var count = Math.Min(8, perBlock - 1 - group * 8);
                output.AddRange(frame.AsSpan(0, count * ch));
            }
        }
        return output.ToArray();
    }

    /// <summary>16-bit PCM WAV.</summary>
    public static byte[] Encode(PcmAudio audio)
    {
        var dataSize = audio.Samples.Length * 2;
        var bytes = new byte[44 + dataSize];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], TagPcm);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short) audio.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], audio.SampleRate * audio.Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short) (audio.Channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);
        for (var i = 0; i < audio.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short) Math.Clamp(MathF.Round(audio.Samples[i] * 32767f), short.MinValue, short.MaxValue));
        return bytes;
    }
}
