using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace FModelMac.Rendering;

public enum NotifyKind
{
    Effect,
    Sound,
    Other
}

/// <summary>
/// An animation notify, on the animation's own timeline: something the game does at that time, such as spawning a
/// particle effect at a socket or playing a sound. <see cref="Duration"/> is set for notify states (timed effects).
/// </summary>
/// <param name="Asset">The effect or sound it plays, by name.</param>
/// <param name="AssetPath">Object path of that sound or effect, to load it.</param>
/// <param name="Socket">Socket or bone it attaches to.</param>
public sealed record ViewerNotify(string Name, float Time, float Duration, NotifyKind Kind, string? Asset, string? AssetPath, string? Socket)
{
    public string Description => Kind switch
    {
        NotifyKind.Effect => $"{Name}: {Asset}" + (Socket is null ? "" : $" at {Socket}"),
        NotifyKind.Sound => $"{Name}: {Asset}",
        _ => Name
    } + $" · {Time:0.00} s" + (Duration > 0 ? $" for {Duration:0.00} s" : "");
}

/// <summary>
/// Reads the notifies of an animation without knowing the game's notify classes: whatever a notify object (or notify
/// state) references is looked at by class, so UE's AnimNotify_PlayNiagaraEffect / PlayParticleEffect / PlaySound and
/// a game's own (DSR's DSRAnimNotify_PlayNiagaraEffect, FF VII's StAnimNotify_SoundEvent…) read the same way: a
/// Niagara or Cascade system is an effect, a sound asset a sound, a Name property mentioning Socket or Bone the
/// attach point. Sounds named only by a string (an event name) are matched to a package of that name when the game
/// has one.
/// </summary>
public static class NotifyReader
{
    public static ViewerNotify[] Read(UAnimationAsset asset, ViewerAnimation animation, Func<string, string?>? soundByName)
    {
        // A sequence's notifies are its own; montages and composites also fire those of the sequences they play
        var sources = new List<(UAnimSequenceBase Source, float Offset, float Rate, float End)>();
        if (asset is UAnimSequenceBase own)
        {
            var rate = own is UAnimSequence { RateScale: > 0 } s ? s.RateScale : 1;
            sources.Add((own, 0, own is UAnimSequence ? rate : 1, float.MaxValue));
        }
        if (asset is not UAnimSequence)
            foreach (var segment in animation.Segments)
                if (segment.Sequence.OriginalSequence is { } sequence)
                    sources.Add((sequence, segment.StartTime, segment.RateScale, segment.EndTime));

        var notifies = new List<ViewerNotify>();
        foreach (var (source, offset, rate, end) in sources)
        {
            foreach (var ev in source.Notifies ?? [])
            {
                try
                {
                    var time = offset + (ev.GetTime() + ev.TriggerTimeOffset) / rate;
                    if (time > end + 1e-3f) continue;
                    var notify = Describe(ev, time, ev.Duration / rate, soundByName);
                    if (notify is null) continue;
                    if (notifies.Any(n => n.Name == notify.Name && Math.Abs(n.Time - notify.Time) < 0.01f)) continue;
                    notifies.Add(notify);
                }
                catch (Exception e)
                {
                    Serilog.Log.Warning(e, "Skipping a notify of {Animation}", source.Name);
                }
            }
        }
        return notifies.OrderBy(n => n.Time).ToArray();
    }

    private static ViewerNotify? Describe(FAnimNotifyEvent ev, float time, float duration, Func<string, string?>? soundByName)
    {
        UObject? notify = null;
        if (ev.Notify is { IsNull: false } pointer) pointer.TryLoad(out notify);
        if (notify is null && ev.NotifyStateClass is { IsNull: false } state) state.TryLoad(out notify);

        var eventName = ev.NotifyName?.Text is { Length: > 0 } n && n != "None" ? n : null;
        var className = notify?.ExportType;
        var name = eventName ?? Trim(className) ?? "Notify";
        if (notify is null) return new ViewerNotify(name, time, duration, NotifyKind.Other, null, null, null);

        var found = new Found();
        foreach (var property in notify.Properties) Visit(property.Name.Text, property.Tag?.GenericValue, found, 0);

        // A sound given only by its event name: a package of that name (or "<name>_Cue") holding a sound
        if (found.Sound is null && soundByName is not null)
        {
            foreach (var candidate in found.Strings)
            {
                if (soundByName(candidate) is not { } path) continue;
                found.Sound = (candidate, path);
                break;
            }
        }

        if (found.Effect is var (effect, effectPath))
            return new ViewerNotify(name, time, duration, NotifyKind.Effect, effect, effectPath, found.Socket);
        if (found.Sound is var (sound, soundPath))
            return new ViewerNotify(name, time, duration, NotifyKind.Sound, sound, soundPath, found.Socket);
        return new ViewerNotify(name, time, duration, NotifyKind.Other, null, null, found.Socket);
    }

    /// <summary>The objects an object references (hard or soft) whose class passes <paramref name="filter"/>.</summary>
    public static List<UObject> References(UObject obj, Func<string, bool> filter)
    {
        var found = new List<UObject>();
        void Walk(object? value, int depth)
        {
            if (depth > 6) return;
            switch (value)
            {
                case FPackageIndex { IsNull: false } pointer when pointer.ResolvedObject is { } resolved:
                    if (resolved.Class?.Name.Text is { } cls && filter(cls) && pointer.TryLoad(out UObject target)) found.Add(target);
                    break;
                case FSoftObjectPath path when !path.AssetPathName.IsNone:
                    if (path.TryLoad(out UObject soft) && filter(soft.ExportType)) found.Add(soft);
                    break;
                case UScriptArray array:
                    foreach (var item in array.Properties) Walk(item.GenericValue, depth + 1);
                    break;
                case FScriptStruct { StructType: FStructFallback fallback }:
                    foreach (var property in fallback.Properties) Walk(property.Tag?.GenericValue, depth + 1);
                    break;
                case FScriptStruct { StructType: FSoftObjectPath path }:
                    Walk(path, depth + 1);
                    break;
            }
        }
        foreach (var property in obj.Properties) Walk(property.Tag?.GenericValue, 0);
        return found;
    }

    private static string? Trim(string? className) => className?
        .Replace("AnimNotifyState_", "").Replace("AnimNotify_", "");

    private sealed class Found
    {
        public (string Name, string Path)? Effect, Sound;
        public string? Socket;
        public readonly List<string> Strings = [];
    }

    private static void Visit(string name, object? value, Found found, int depth)
    {
        if (depth > 4) return;
        switch (value)
        {
            case FPackageIndex { IsNull: false } pointer when pointer.ResolvedObject is { } resolved:
                Classify(resolved.Class?.Name.Text, resolved.Name.Text, resolved.GetPathName(), found);
                break;
            case FSoftObjectPath path when !path.AssetPathName.IsNone && path.AssetPathName.Text is { Length: > 0 } text:
                // A soft reference doesn't say what it points to: load it to see (notifies reference few assets)
                if (path.TryLoad(out UObject target)) Classify(target.ExportType, target.Name, target.GetPathName(), found);
                break;
            case FName text when !text.IsNone:
                Text(name, text.Text, found);
                break;
            case string text:
                Text(name, text, found);
                break;
            case UScriptArray array:
                foreach (var item in array.Properties) Visit(name, item.GenericValue, found, depth + 1);
                break;
            case FScriptStruct { StructType: FStructFallback fallback }:
                foreach (var property in fallback.Properties) Visit(property.Name.Text, property.Tag?.GenericValue, found, depth + 1);
                break;
            case FScriptStruct { StructType: FSoftObjectPath path }:
                Visit(name, path, found, depth + 1);
                break;
        }
    }

    private static void Text(string property, string value, Found found)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "None") return;
        if (property.Contains("Socket", StringComparison.OrdinalIgnoreCase) || property.Contains("Bone", StringComparison.OrdinalIgnoreCase))
            found.Socket ??= value;
        else if (value.Length > 2)
            found.Strings.Add(value);
    }

    private static void Classify(string? className, string name, string path, Found found)
    {
        if (className is null) return;
        if (className.Contains("Niagara", StringComparison.OrdinalIgnoreCase) && className.EndsWith("System", StringComparison.OrdinalIgnoreCase)
            || className.Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase))
            found.Effect ??= (name, path);
        else if (IsSound(className))
            found.Sound ??= (name, path);
    }

    // Sound assets, not their settings (attenuation, class, concurrency, mixes)
    public static bool IsSound(string className) =>
        className is "SoundWave" or "SoundCue" or "SoundNodeWave" or "MetaSoundSource" or "AkAudioEvent" or "FMODEvent"
            or "SoundAtomCue" or "AtomCue" or "SQEXSEADSound" ||
        className.StartsWith("SoundWave", StringComparison.Ordinal) || className.EndsWith("SoundCue", StringComparison.Ordinal);
}
