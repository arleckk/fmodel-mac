using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Writers.ActorX.Structs.Animations;

namespace FModel.Rendering;

/// <summary>Something the viewer can play: one animation, or a blend space mixing several.</summary>
public interface IViewerMotion
{
    string Name { get; }
    string SkeletonName { get; }
    IReadOnlyList<ViewerAnimation> Clips { get; }
}

/// <summary>
/// A decoded animation asset (sequence, montage or composite): CUE4Parse's <see cref="CAnimSet"/> plus
/// the timing Snooper derives from it, and its float curves (they drive morph targets by name).
/// Tracks are indexed by the animation skeleton's bones.
/// </summary>
public sealed class ViewerAnimation : IViewerMotion
{
    public sealed record Segment(CAnimSequence Sequence, float StartTime, float EndTime, float RateScale, float SecondsPerFrame, FFloatCurve[] Curves);

    public required string Name { get; init; }
    public required string SkeletonName { get; init; }
    public required CAnimSet Set { get; init; }
    public required Segment[] Segments { get; init; }
    public float Duration => Segments.Length > 0 ? Segments[^1].EndTime : 0;
    public int FrameCount => Segments.Sum(s => s.Sequence.NumFrames);
    public float FrameRate => Segments.Length > 0 ? Segments[0].Sequence.FramesPerSecond : 30;
    public IReadOnlyList<ViewerAnimation> Clips => [this];
    /// <summary>Effects, sounds and other events on this animation's timeline.</summary>
    public ViewerNotify[] Notifies { get; private set; } = [];

    /// <param name="soundByName">Finds a sound package by name, for notifies that only name their sound event.</param>
    public static ViewerAnimation FromExport(UAnimationAsset asset, Func<string, string?>? soundByName = null)
    {
        var set = asset.ConvertAnims();
        var segments = set.Sequences
            .Where(s => s.Tracks is { Count: > 0 } && s.NumFrames > 0)
            .Select(s =>
            {
                var rate = s.OriginalSequence.RateScale is > 0 and var r ? r : 1;
                var curves = (s.OriginalSequence.CompressedCurveData ?? s.OriginalSequence.RawCurveData)?.FloatCurves ?? [];
                return new Segment(s, s.StartPos, s.StartPos + s.AnimEndTime / rate, rate, s.AnimEndTime / s.NumFrames,
                    curves.Where(c => c.FloatCurve is not null).ToArray());
            })
            .ToArray();
        if (segments.Length == 0) throw new InvalidOperationException($"{asset.Name} has no animation tracks");

        var animation = new ViewerAnimation { Name = asset.Name, SkeletonName = set.Skeleton.Name, Set = set, Segments = segments };
        try
        {
            animation.Notifies = NotifyReader.Read(asset, animation, soundByName);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Could not read the notifies of {Animation}", asset.GetPathName());
        }
        return animation;
    }
}

/// <summary>
/// A blend space: sample animations placed on a 1D or 2D parameter grid. Weights come from the cooked
/// <see cref="UBlendSpaceBase.GridSamples"/>, as UE does at runtime, with an interpolation fallback when
/// the grid is missing (UE5 triangulated blend spaces).
/// </summary>
public sealed class ViewerBlendSpace : IViewerMotion
{
    public sealed record Axis(string Name, float Min, float Max, int GridNum);
    /// <summary>A grid point: up to 3 samples and their weights (UE's FEditorElement, remapped to loaded clips).</summary>
    public sealed record GridElement(int[] Indices, float[] Weights);

    public required string Name { get; init; }
    public required string SkeletonName { get; init; }
    public required Axis[] Axes { get; init; }
    public required ViewerAnimation[] Samples { get; init; }
    public required Vector2[] SampleValues { get; init; }
    public required float[] SampleRates { get; init; }
    public required GridElement[] Grid { get; init; }
    public IReadOnlyList<ViewerAnimation> Clips => Samples;

    public static ViewerBlendSpace FromExport(UBlendSpaceBase blendSpace)
    {
        var samples = new List<ViewerAnimation>();
        var values = new List<Vector2>();
        var rates = new List<float>();
        var remap = new int[blendSpace.SampleData.Length]; // sample index -> loaded clip index, -1 when unusable
        string? firstError = null;
        var decoded = new Dictionary<string, ViewerAnimation>(StringComparer.OrdinalIgnoreCase); // samples often reuse a clip
        for (var i = 0; i < blendSpace.SampleData.Length; i++)
        {
            var sample = blendSpace.SampleData[i];
            remap[i] = -1;
            if (sample.Animation is null || !sample.Animation.TryLoad<UAnimSequence>(out var sequence)) continue;
            try
            {
                var path = sequence.GetPathName();
                if (!decoded.TryGetValue(path, out var clip)) decoded[path] = clip = ViewerAnimation.FromExport(sequence);
                samples.Add(clip);
                values.Add(new Vector2(sample.SampleValue.X, sample.SampleValue.Y));
                rates.Add(sample.RateScale > 0 ? sample.RateScale : 1);
                remap[i] = samples.Count - 1;
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Skipping blend space sample {Sample}", sample.Animation.Name);
                firstError ??= $"{sample.Animation.Name}: {e.Message}";
            }
        }
        if (samples.Count == 0)
            throw new InvalidOperationException($"{blendSpace.Name} has no playable samples" + (firstError is null ? "" : $" ({firstError})"));

        var is1D = blendSpace is UBlendSpace1D;
        var parameters = blendSpace.BlendParameters ?? [];
        var axes = Enumerable.Range(0, is1D ? 1 : 2).Select(i =>
        {
            var p = i < parameters.Length ? parameters[i] : new FBlendParameter();
            var name = string.IsNullOrEmpty(p.DisplayName) || p.DisplayName == "None" ? (i == 0 ? "X" : "Y") : p.DisplayName;
            return new Axis(name, p.Min, p.Max > p.Min ? p.Max : p.Min + 100, p.GridNum > 0 ? p.GridNum : 4);
        }).ToArray();

        // Grid elements reference SampleData indices; point them at the clips that actually loaded
        var grid = blendSpace.GridSamples.Select(e => new GridElement(
            e.Indices.Select(index => index >= 0 && index < remap.Length ? remap[index] : -1).ToArray(), e.Weights)).ToArray();

        return new ViewerBlendSpace
        {
            Name = blendSpace.Name, SkeletonName = samples[0].SkeletonName, Axes = axes, Samples = samples.ToArray(),
            SampleValues = values.ToArray(), SampleRates = rates.ToArray(),
            Grid = grid
        };
    }

    /// <summary>Per-sample weights (summing to 1) for a point in parameter space.</summary>
    public float[] Weights(float x, float y)
    {
        var weights = new float[Samples.Length];
        var nx = Axes[0].GridNum;
        var gx = Math.Clamp((x - Axes[0].Min) / (Axes[0].Max - Axes[0].Min), 0, 1) * nx;

        if (Axes.Length == 1 && Grid.Length == nx + 1)
        {
            var x0 = Math.Min((int) gx, nx - 1);
            Add(weights, Grid[x0], 1 - (gx - x0));
            Add(weights, Grid[x0 + 1], gx - x0);
        }
        else if (Axes.Length == 2 && Grid.Length == (nx + 1) * (Axes[1].GridNum + 1))
        {
            // UBlendSpace::GetEditorElement: X-major grid
            var ny = Axes[1].GridNum;
            var gy = Math.Clamp((y - Axes[1].Min) / (Axes[1].Max - Axes[1].Min), 0, 1) * ny;
            var x0 = Math.Min((int) gx, nx - 1);
            var y0 = Math.Min((int) gy, ny - 1);
            float fx = gx - x0, fy = gy - y0;
            Add(weights, Grid[x0 * (ny + 1) + y0], (1 - fx) * (1 - fy));
            Add(weights, Grid[(x0 + 1) * (ny + 1) + y0], fx * (1 - fy));
            Add(weights, Grid[x0 * (ny + 1) + y0 + 1], (1 - fx) * fy);
            Add(weights, Grid[(x0 + 1) * (ny + 1) + y0 + 1], fx * fy);
        }
        else
        {
            FallbackWeights(weights, x, y);
        }

        var total = weights.Sum();
        if (total <= 1e-6f) weights[0] = total = 1;
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;
        return weights;
    }

    private static void Add(float[] weights, GridElement element, float amount)
    {
        if (amount <= 0) return;
        for (var i = 0; i < element.Indices.Length && i < element.Weights.Length; i++)
            if (element.Indices[i] >= 0 && element.Indices[i] < weights.Length)
                weights[element.Indices[i]] += element.Weights[i] * amount;
    }

    // No cooked grid: linear between the neighbors in 1D, inverse distance weighting in 2D
    private void FallbackWeights(float[] weights, float x, float y)
    {
        if (Axes.Length == 1)
        {
            var order = Enumerable.Range(0, Samples.Length).OrderBy(i => SampleValues[i].X).ToArray();
            if (x <= SampleValues[order[0]].X) { weights[order[0]] = 1; return; }
            if (x >= SampleValues[order[^1]].X) { weights[order[^1]] = 1; return; }
            for (var i = 0; i + 1 < order.Length; i++)
            {
                float a = SampleValues[order[i]].X, b = SampleValues[order[i + 1]].X;
                if (x < a || x > b) continue;
                var t = b > a ? (x - a) / (b - a) : 0;
                weights[order[i]] = 1 - t;
                weights[order[i + 1]] = t;
                return;
            }
            return;
        }

        Vector2 Normalize(Vector2 v) => new((v.X - Axes[0].Min) / (Axes[0].Max - Axes[0].Min), (v.Y - Axes[1].Min) / (Axes[1].Max - Axes[1].Min));
        var point = Normalize(new Vector2(x, y));
        for (var i = 0; i < Samples.Length; i++)
        {
            var distance = Vector2.DistanceSquared(point, Normalize(SampleValues[i]));
            if (distance < 1e-8f)
            {
                Array.Clear(weights);
                weights[i] = 1;
                return;
            }
            weights[i] = 1 / distance;
        }
    }
}

/// <summary>
/// One or more clips bound to a model by bone name (like Snooper, no retargeting), blended by weight.
/// Time is a normalized phase so samples of different lengths stay in sync, as blend spaces do in UE.
/// <see cref="Evaluate"/> produces one skinning matrix per mesh bone (inverse reference global ×
/// animated global, in viewer space) and the blended curve values.
/// </summary>
public sealed class AnimationPose
{
    private readonly ViewerModel _model;
    private readonly ViewerAnimation[] _clips;
    private readonly float[] _clipRates;
    private readonly int[][] _skeletonIndex;   // per clip: mesh bone -> animation skeleton bone, or -1
    private readonly Matrix4x4[] _restLocal, _inverseRestGlobal;

    public Matrix4x4[] Globals { get; }        // animated bone transforms after the last Evaluate
    public Matrix4x4[] Skinning { get; }
    public Dictionary<string, float> Curves { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int MappedBones { get; }
    public IViewerMotion Motion { get; }

    /// <summary>Blend weight per clip, summing to 1. A single animation is just [1].</summary>
    public float[] Weights { get; set; }

    /// <summary>
    /// When false (the default) root bones keep their reference position, so attacks and dashes that
    /// move the whole character (root motion) play in place instead of leaving the camera.
    /// </summary>
    public bool RootMotion { get; set; }

    /// <summary>Length of one cycle at the current weights, in seconds.</summary>
    public float Duration
    {
        get
        {
            var duration = 0f;
            for (var i = 0; i < _clips.Length; i++) duration += Weights[i] * _clips[i].Duration / _clipRates[i];
            return Math.Max(duration, 1e-3f);
        }
    }

    public AnimationPose(ViewerModel model, IViewerMotion motion)
    {
        var bones = model.Bones ?? throw new ArgumentException("The model has no skeleton", nameof(model));
        _model = model;
        Motion = motion;
        _clips = motion.Clips.ToArray();
        _clipRates = motion is ViewerBlendSpace blendSpace ? blendSpace.SampleRates : _clips.Select(_ => 1f).ToArray();
        Weights = _clips.Select((_, i) => i == 0 ? 1f : 0f).ToArray();

        _skeletonIndex = _clips.Select(clip =>
        {
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var reference = clip.Set.Skeleton.ReferenceSkeleton.FinalRefBoneInfo;
            for (var i = 0; i < reference.Length; i++) byName.TryAdd(reference[i].Name.Text, i);
            return bones.Select(b => byName.GetValueOrDefault(b.Name, -1)).ToArray();
        }).ToArray();
        MappedBones = Enumerable.Range(0, bones.Length).Count(b => _skeletonIndex.Any(map => map[b] >= 0));

        _restLocal = new Matrix4x4[bones.Length];
        _inverseRestGlobal = new Matrix4x4[bones.Length];
        Globals = new Matrix4x4[bones.Length];
        Skinning = new Matrix4x4[bones.Length];

        var restGlobal = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            // Snooper keeps the root unscaled
            _restLocal[i] = Local(bone.Rotation, bone.Position, bone.ParentIndex < 0 ? FVector.OneVector : bone.Scale);
            restGlobal[i] = bone.ParentIndex >= 0 ? _restLocal[i] * restGlobal[bone.ParentIndex] : _restLocal[i];
            Matrix4x4.Invert(restGlobal[i], out _inverseRestGlobal[i]);
            Globals[i] = restGlobal[i];
            Skinning[i] = Matrix4x4.Identity;
        }
    }

    /// <summary>Bone lines (from each bone to its parent) of the reference pose, for skeletons without an animation.</summary>
    public static Matrix4x4[] ReferenceGlobals(ViewerModel model)
    {
        var bones = model.Bones ?? [];
        var globals = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            var local = Local(b.Rotation, b.Position, b.ParentIndex < 0 ? FVector.OneVector : b.Scale);
            globals[i] = b.ParentIndex >= 0 ? local * globals[b.ParentIndex] : local;
        }
        return globals;
    }

    /// <param name="phase">Position in the cycle, 0 to 1.</param>
    public void Evaluate(float phase)
    {
        var bones = _model.Bones!;
        Curves.Clear();

        // Locate each active clip's segment and frame once
        var active = new List<(int Clip, float Weight, ViewerAnimation.Segment Segment, float Frame)>();
        for (var c = 0; c < _clips.Length; c++)
        {
            if (Weights[c] <= 1e-4f) continue;
            var clip = _clips[c];
            var time = Math.Clamp(phase, 0, 1) * clip.Duration;
            var segment = clip.Segments.FirstOrDefault(s => time >= s.StartTime && time <= s.EndTime) ?? clip.Segments[^1];
            var frame = Math.Clamp((time - segment.StartTime) * segment.RateScale / segment.SecondsPerFrame, 0, segment.Sequence.NumFrames - 1);
            active.Add((c, Weights[c], segment, frame));

            var sequenceTime = frame * segment.SecondsPerFrame;
            foreach (var curve in segment.Curves)
            {
                var name = curve.CurveName.Text;
                Curves[name] = Curves.GetValueOrDefault(name) + Weights[c] * curve.FloatCurve.Eval(sequenceTime);
            }
        }

        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            Matrix4x4 local;
            if (active.Count == 0)
            {
                local = _restLocal[i];
            }
            else
            {
                // Weighted blend of each clip's local transform: lerp positions/scales, nlerp rotations
                var position = Vector3.Zero;
                var scale = Vector3.Zero;
                var rotation = new Quaternion(0, 0, 0, 0);
                Quaternion? reference = null;
                foreach (var (c, weight, segment, frame) in active)
                {
                    var (q, p, s) = SampleLocal(bone, _skeletonIndex[c][i], segment, frame);
                    var quat = new Quaternion(q.X, q.Y, q.Z, q.W);
                    if (reference is null) reference = quat;
                    else if (Quaternion.Dot(reference.Value, quat) < 0) quat = Quaternion.Negate(quat);
                    rotation += quat * weight;
                    position += new Vector3(p.X, p.Y, p.Z) * weight;
                    scale += new Vector3(s.X, s.Y, s.Z) * weight;
                }
                rotation = Quaternion.Normalize(rotation);
                if (bone.ParentIndex < 0)
                {
                    if (!RootMotion) position = new Vector3(bone.Position.X, bone.Position.Y, bone.Position.Z);
                    scale = Vector3.One; // Snooper keeps the root unscaled
                }
                local = Local(new FQuat(rotation.X, rotation.Y, rotation.Z, rotation.W),
                    new FVector(position.X, position.Y, position.Z), new FVector(scale.X, scale.Y, scale.Z));
            }

            Globals[i] = bone.ParentIndex >= 0 ? local * Globals[bone.ParentIndex] : local;
            Skinning[i] = _inverseRestGlobal[i] * Globals[i];
        }
    }

    // A bone's local transform in one clip (UE space), or its reference pose when the clip doesn't animate it
    private static (FQuat, FVector, FVector) SampleLocal(ViewerBone bone, int skeletonIndex, ViewerAnimation.Segment segment, float frame)
    {
        var rotation = bone.Rotation;
        var position = bone.Position;
        var scale = bone.Scale;
        var sequence = segment.Sequence;
        if (skeletonIndex >= 0 && skeletonIndex < sequence.Tracks.Count && sequence.OriginalSequence.FindTrackForBoneIndex(skeletonIndex) >= 0)
            sequence.Tracks[skeletonIndex].GetBoneTransform(frame, sequence.NumFrames, ref rotation, ref position, ref scale);
        return (rotation, position, scale);
    }

    // Same swizzle as the vertices and as Snooper's Transform
    private static Matrix4x4 Local(FQuat rotation, FVector position, FVector scale) => ViewerMath.ToViewer(rotation, position, scale);
}
