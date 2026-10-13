using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.Borderlands3.Assets.Exports;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Exports.Wwise;
using CUE4Parse.GameTypes.SquareEnix.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Criware;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Criware;
using CUE4Parse.UE4.Criware.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.Wwise;
using CUE4Parse_Conversion.Sounds;
using Newtonsoft.Json;

namespace FModelMac.Services.Audio;

/// <summary>A sound found in a package or file, still in its original format.</summary>
/// <param name="Path">Game path without extension, used to place it under Exports.</param>
/// <param name="Extension">Original format (wem, ogg, wav, binka…), lower case.</param>
public sealed record ExtractedSound(string Name, string Path, string Extension, byte[] Data)
{
    public string FileName => $"{Name}.{Extension}";
}

/// <summary>
/// FModel's sound handling (CUE4ParseViewModel's audio cases): sound waves, Wwise media/events/banks, FMOD events
/// and banks, CRIWARE cue sheets and wave banks, Square Enix SEAD banks, and loose audio files.
/// </summary>
public sealed class SoundExtractor(AbstractVfsFileProvider provider, string gameDirectory)
{
    private readonly Lazy<WwiseProvider> _wwise = new(() => new WwiseProvider(provider, gameDirectory));
    private readonly Lazy<FModProvider> _fmod = new(() => new FModProvider(provider, gameDirectory));
    private readonly Lazy<CriWareProvider> _criware = new(() => new CriWareProvider(provider, gameDirectory));

    /// <summary>Loose files that are sounds or sound banks.</summary>
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bnk", "pck", "bank", "awb", "acb", "wem", "wav", "ogg", "flac", "at9", "xvag", "binka", "hca", "adx", "opus"
    };

    /// <summary>Problems worth telling the user about (missing vgmstream, unsupported formats), from the last call.</summary>
    public List<string> Warnings { get; } = [];

    public List<ExtractedSound> FromExports(GameFile file, IReadOnlyList<UObject> exports)
    {
        Warnings.Clear();
        var sounds = new List<ExtractedSound>();
        var folder = Folder(file.PathWithoutExtension);

        void AddWwise(IEnumerable<WwiseExtractedSound> extracted)
        {
            foreach (var s in extracted)
            {
                var data = s.GetData();
                if (data.Length > 0) sounds.Add(Named(s.OutputPath, s.Extension, data));
            }
        }

        void AddDecoded(UObject export, string path)
        {
            export.Decode(true, out var format, out var data);
            if (data is { Length: > 0 } && !string.IsNullOrEmpty(format))
                sounds.Add(Named(path, format, data));
            else if (!string.IsNullOrEmpty(format))
                Warnings.Add($"Unsupported audio format '{format}' in {export.Name}");
        }

        foreach (var export in exports)
        {
            try
            {
                switch (export)
                {
                    case UExternalSource external:
                        if (external.Data?.WemFile?.GetData() is { Length: > 0 } wem)
                            sounds.Add(Named($"{folder}/{Path.GetFileNameWithoutExtension(external.ExternalSourcePath)}", "wem", wem));
                        break;
                    case UAkAudioBank bank:
                        AddWwise(_wwise.Value.ExtractBankSounds(bank));
                        break;
                    case UAkAudioEvent audioEvent:
                        AddWwise(_wwise.Value.ExtractAudioEventSounds(audioEvent));
                        break;
                    case UFMODEvent fmodEvent:
                        sounds.AddRange(_fmod.Value.ExtractEventSounds(fmodEvent).Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)));
                        break;
                    case UFMODBank fmodBank:
                        sounds.AddRange(_fmod.Value.ExtractBankSounds(fmodBank).Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)));
                        break;
                    case USoundAtomCueSheet or UAtomCueSheet or USoundAtomCue or UAtomWaveBank:
                        var cri = export switch
                        {
                            USoundAtomCueSheet cueSheet => _criware.Value.ExtractCriWareSounds(cueSheet),
                            UAtomCueSheet cueSheet => _criware.Value.ExtractCriWareSounds(cueSheet),
                            USoundAtomCue cue => _criware.Value.ExtractCriWareSounds(cue),
                            _ => _criware.Value.ExtractCriWareSounds((UAtomWaveBank) export)
                        };
                        sounds.AddRange(cri.Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)));
                        break;
                    case USQEXSEADSoundBank or USQEXSEADSound:
                        var sqex = export switch
                        {
                            USQEXSEADSoundBank b => b.SQEXSoundBankData?.ReadDataOnce() ?? [],
                            _ => ((USQEXSEADSound) export).SQEXSoundData?.ReadDataOnce() ?? []
                        };
                        if (sqex.Length > 0)
                            sounds.AddRange(Vgmstream.ExtractSubsongs(sqex, "sab").Select(s => Named($"{folder}/{s.Name}", "wav", s.Wav)));
                        break;
                    // Media data inside a UAkMediaAsset is handled through the asset (it has the real name)
                    case UAkMediaAssetData mediaData when mediaData.Outer?.Object?.Value is UAkMediaAsset:
                        break;
                    case UAkMediaAssetData or USoundWave or USoundNodeWave:
                        AddDecoded(export, file.PathWithoutExtension);
                        break;
                    case UAkMediaAsset media:
                        if (media.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData data)
                            AddDecoded(data, $"{folder}/{(string.IsNullOrEmpty(media.MediaName) ? media.Name : media.MediaName)}");
                        break;
                    case UAkAudioEventData eventData:
                        foreach (var item in eventData.MediaList)
                        {
                            if (item?.Object?.Value is not UAkMediaAsset asset ||
                                asset.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is not UAkMediaAssetData assetData) continue;
                            var name = string.IsNullOrEmpty(asset.MediaName) ? $"{eventData.Outer?.Name} ({asset.ID})" : asset.MediaName;
                            AddDecoded(assetData, $"{folder}/{name}");
                        }
                        break;
                    case UDialogPerformanceData dialog:
                        AddWwise(_wwise.Value.ExtractDialogBorderlands3(dialog));
                        break;
                }
            }
            catch (MissingDecoderException e)
            {
                Warnings.Add(e.Message);
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not extract audio from {Export}", export.GetPathName());
                Warnings.Add($"{export.Name}: {e.Message}");
            }
        }
        return sounds;
    }

    /// <summary>Loose audio files and sound banks; <paramref name="json"/> describes a bank, like FModel's tab.</summary>
    public List<ExtractedSound> FromFile(GameFile file, out string? json)
    {
        Warnings.Clear();
        json = null;
        var folder = Folder(file.Path);
        switch (file.Extension.ToLowerInvariant())
        {
            case "bank":
            {
                using var archive = file.CreateReader();
                if (!_fmod.Value.TryLoadBank(archive, file.NameWithoutExtension, out var reader))
                    throw new InvalidDataException($"Not a valid FMOD bank: {file.Name}");
                json = JsonConvert.SerializeObject(reader, Formatting.Indented);
                return _fmod.Value.ExtractBankSounds(reader).Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)).ToList();
            }
            case "bnk":
            case "pck":
            {
                using var archive = file.CreateReader();
                var wwise = new WwiseReader(new FWwiseArchive(archive), new WwiseGameFileSource(file));
                json = JsonConvert.SerializeObject(wwise, Formatting.Indented);
                return _wwise.Value.ExtractBankSounds(wwise)
                    .Select(s => (s, data: s.GetData())).Where(x => x.data.Length > 0)
                    .Select(x => Named(x.s.OutputPath, x.s.Extension, x.data)).ToList();
            }
            case "awb":
            {
                using var archive = file.CreateReader();
                var awb = new AwbReader(archive);
                json = JsonConvert.SerializeObject(awb, Formatting.Indented);
                return _criware.Value.ExtractCriWareSounds(awb, archive.Name).Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)).ToList();
            }
            case "acb":
            {
                using var archive = file.CreateReader();
                var acb = new AcbReader(archive);
                json = JsonConvert.SerializeObject(acb, Formatting.Indented);
                return _criware.Value.ExtractCriWareSounds(acb, archive.Name).Select(s => Named($"{folder}/{s.Name}", s.Extension, s.Data)).ToList();
            }
            default:
                return [new ExtractedSound(file.NameWithoutExtension, file.PathWithoutExtension, file.Extension.ToLowerInvariant(), file.Read())];
        }
    }

    private static ExtractedSound Named(string path, string extension, byte[] data)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        return new ExtractedSound(Path.GetFileName(path), path, extension.ToLowerInvariant(), data);
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
