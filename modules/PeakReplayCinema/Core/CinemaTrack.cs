using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PeakReplayLab.Cinema;

[JsonObject(MemberSerialization.OptIn)]
public sealed class CinemaTrack
{
    [JsonProperty("keyframes", Required = Required.Always)] private readonly List<CinemaKeyframe> keyframes;
    private readonly IReadOnlyList<CinemaKeyframe> readonlyView;
    public IReadOnlyList<CinemaKeyframe> Keyframes => readonlyView;
    public int Count => keyframes.Count;
    public CinemaTrack() : this(new List<CinemaKeyframe>()) { }
    [JsonConstructor]
    public CinemaTrack(List<CinemaKeyframe> keyframes)
    {
        if (keyframes == null) throw new ArgumentNullException(nameof(keyframes));
        if (keyframes.Count > CinemaLimits.MaxKeyframes) throw new ArgumentException("Too many camera keyframes.");
        this.keyframes = new List<CinemaKeyframe>(keyframes);
        readonlyView = this.keyframes.AsReadOnly();
        foreach (var key in this.keyframes) if (key == null) throw new ArgumentException("Null camera keyframe.");
        this.keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        Validate();
    }
    public void Validate()
    {
        if (keyframes.Count > CinemaLimits.MaxKeyframes) throw new ArgumentException("Too many camera keyframes.");
        double previous = -1;
        foreach (var key in keyframes)
        {
            if (key == null || key.Time <= previous) throw new ArgumentException("Duplicate or unordered camera time.");
            CinemaLimits.Range(key.Time, 0, CinemaLimits.MaxTime, nameof(key.Time));
            CinemaLimits.Text(key.SegmentId, 128, nameof(key.SegmentId));
            CinemaLimits.EnumValue(key.Transition, nameof(key.Transition)); key.Pose.Validate();
            previous = key.Time;
        }
    }
    public void Upsert(CinemaKeyframe key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        int index = LowerBound(key.Time);
        if (index < keyframes.Count && keyframes[index].Time == key.Time) { keyframes[index] = key; return; }
        if (keyframes.Count >= CinemaLimits.MaxKeyframes) throw new InvalidOperationException("Camera keyframe limit reached.");
        keyframes.Insert(index, key);
    }
    // Presets are inserted transactionally: a limit failure cannot leave half a camera path.
    public void UpsertRange(IEnumerable<CinemaKeyframe> keys)
    {
        if (keys == null) throw new ArgumentNullException(nameof(keys));
        var next = new CinemaTrack(new List<CinemaKeyframe>(keyframes));
        int count = 0;
        foreach (var key in keys)
        {
            if (++count > CinemaLimits.MaxKeyframes) throw new InvalidOperationException("Too many incoming camera keys.");
            next.Upsert(key);
        }
        keyframes.Clear(); keyframes.AddRange(next.keyframes);
    }
    public bool RemoveAt(double time)
    {
        CinemaLimits.Range(time, 0, CinemaLimits.MaxTime, nameof(time));
        int index = LowerBound(time);
        if (index >= keyframes.Count || keyframes[index].Time != time) return false;
        keyframes.RemoveAt(index); return true;
    }
    public void Clear() => keyframes.Clear();
    public bool TrySample(double time, string segmentId, out CinemaPose pose)
    {
        CinemaLimits.Text(segmentId, 128, nameof(segmentId));
        return Sample(time, segmentId, out pose);
    }
    public bool TrySample(double time, out CinemaPose pose) => Sample(time, null, out pose);
    private bool Sample(double time, string? segmentId, out CinemaPose pose)
    {
        pose = default;
        if (!CinemaLimits.Finite(time) || keyframes.Count == 0) return false;
        int next = LowerBound(time);
        CinemaKeyframe left;
        if (next == 0) left = keyframes[0];
        else if (next < keyframes.Count && keyframes[next].Time == time) left = keyframes[next];
        else left = keyframes[next - 1];
        if (segmentId != null && !string.Equals(segmentId, left.SegmentId, StringComparison.Ordinal)) return false;
        pose = left.Pose;
        if (time <= left.Time || next >= keyframes.Count) return true;
        var right = keyframes[next];
        if (!string.Equals(left.SegmentId, right.SegmentId, StringComparison.Ordinal) || left.Transition == CinemaTransition.Cut)
            return true;
        double t = (time - left.Time) / (right.Time - left.Time);
        if (left.Transition == CinemaTransition.Smooth) t = t * t * (3 - 2 * t);
        pose = CinemaMath.Lerp(left.Pose, right.Pose, (float)t); return true;
    }
    private int LowerBound(double time)
    {
        int low = 0, high = keyframes.Count;
        while (low < high) { int middle = low + (high - low) / 2; if (keyframes[middle].Time < time) low = middle + 1; else high = middle; }
        return low;
    }
}

internal static class CinemaMath
{
    internal static CinemaVector3 Add(CinemaVector3 a, CinemaVector3 b) => new CinemaVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    internal static CinemaVector3 Subtract(CinemaVector3 a, CinemaVector3 b) => new CinemaVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static CinemaVector3 Scale(CinemaVector3 value, float scale) => new CinemaVector3(value.X * scale, value.Y * scale, value.Z * scale);
    internal static double Length(CinemaVector3 value) => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
    internal static CinemaVector3 Normalize(CinemaVector3 value)
    {
        double length = Length(value);
        return length < 0.000001 ? new CinemaVector3(0, 0, 1) : Scale(value, (float)(1 / length));
    }
    internal static CinemaVector3 Cross(CinemaVector3 a, CinemaVector3 b) =>
        new CinemaVector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static CinemaVector3 Rotate(CinemaQuaternion q, CinemaVector3 v)
    {
        var axis = new CinemaVector3(q.X, q.Y, q.Z);
        var twiceCross = Scale(Cross(axis, v), 2);
        return Add(v, Add(Scale(twiceCross, q.W), Cross(axis, twiceCross)));
    }
    internal static CinemaQuaternion Slerp(CinemaQuaternion a, CinemaQuaternion b, float t)
    {
        double dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
        float sign = dot < 0 ? -1 : 1;
        dot = Math.Min(1, Math.Max(0, Math.Abs(dot)));
        double wa = 1 - t, wb = t;
        if (dot < 0.9995) { double angle = Math.Acos(dot), sine = Math.Sin(angle); wa = Math.Sin((1 - t) * angle) / sine; wb = Math.Sin(t * angle) / sine; }
        return new CinemaQuaternion((float)(a.X * wa + sign * b.X * wb), (float)(a.Y * wa + sign * b.Y * wb),
            (float)(a.Z * wa + sign * b.Z * wb), (float)(a.W * wa + sign * b.W * wb));
    }
    internal static CinemaQuaternion Multiply(CinemaQuaternion a, CinemaQuaternion b) => new CinemaQuaternion(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    internal static CinemaQuaternion Inverse(CinemaQuaternion q) => new CinemaQuaternion(-q.X, -q.Y, -q.Z, q.W);
    internal static CinemaPose Lerp(CinemaPose a, CinemaPose b, float t) => new CinemaPose(
        new CinemaVector3(a.Position.X + (b.Position.X - a.Position.X) * t,
            a.Position.Y + (b.Position.Y - a.Position.Y) * t, a.Position.Z + (b.Position.Z - a.Position.Z) * t),
        Slerp(a.Rotation, b.Rotation, t), a.FieldOfView + (b.FieldOfView - a.FieldOfView) * t,
        a.FocusDistance + (b.FocusDistance - a.FocusDistance) * t);
    internal static CinemaQuaternion LookAt(CinemaVector3 position, CinemaVector3 center, CinemaQuaternion fallback)
    {
        var delta = Subtract(center, position);
        if (Length(delta) < 0.0001) return fallback;
        var forward = Normalize(delta);
        var up = Math.Abs(forward.Y) > 0.999 ? new CinemaVector3(0, 0, 1) : new CinemaVector3(0, 1, 0);
        var right = Normalize(Cross(up, forward)); up = Cross(forward, right);
        double m00 = right.X, m01 = up.X, m02 = forward.X, m10 = right.Y, m11 = up.Y, m12 = forward.Y,
            m20 = right.Z, m21 = up.Z, m22 = forward.Z;
        double x, y, z, w, trace = m00 + m11 + m22;
        if (trace > 0) { double s = Math.Sqrt(trace + 1) * 2; w = s / 4; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s; }
        else if (m00 > m11 && m00 > m22) { double s = Math.Sqrt(1 + m00 - m11 - m22) * 2; w = (m21 - m12) / s; x = s / 4; y = (m01 + m10) / s; z = (m02 + m20) / s; }
        else if (m11 > m22) { double s = Math.Sqrt(1 + m11 - m00 - m22) * 2; w = (m02 - m20) / s; x = (m01 + m10) / s; y = s / 4; z = (m12 + m21) / s; }
        else { double s = Math.Sqrt(1 + m22 - m00 - m11) * 2; w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = s / 4; }
        return new CinemaQuaternion((float)x, (float)y, (float)z, (float)w);
    }
}
