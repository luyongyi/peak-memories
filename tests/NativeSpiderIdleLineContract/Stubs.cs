namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static implicit operator bool(Object? value) => value != null && !value.Destroyed;
        public static bool operator !(Object? value) => value == null || value.Destroyed;
    }
    public class GameObject : Object
    {
        private readonly Dictionary<Type, Object> components = new();
        public int ComponentReads;
        public void Add<T>(T component) where T : Object => components[typeof(T)] = component;
        public T? GetComponent<T>() where T : Object
        { ComponentReads++; return components.TryGetValue(typeof(T), out var value) ? (T)value : null; }
    }
    public class Transform : Object
    {
        private Vector3 point;
        public int Reads;
        public Vector3 position { get { Reads++; return point; } set => point = value; }
        public Vector3 up = new(0, 1, 0);
    }
    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(float value, Vector3 a) => new(value * a.x, value * a.y, value * a.z);
        public static Vector3 operator *(Vector3 a, float value) => value * a;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float f)
        { f = Math.Clamp(f, 0, 1); return new(a.x + (b.x-a.x)*f, a.y + (b.y-a.y)*f, a.z + (b.z-a.z)*f); }
    }
    public readonly record struct Keyframe(float time, float value);
    public enum WrapMode { Default, Loop, PingPong }
    public class AnimationCurve
    {
        private readonly Keyframe[] values;
        public AnimationCurve(params Keyframe[] values) => this.values = (Keyframe[])values.Clone();
        public Keyframe[] keys => (Keyframe[])values.Clone();
        public WrapMode preWrapMode, postWrapMode;
        public float Evaluate(float time)
        {
            if (values.Length == 0) return 0;
            if (time <= values[0].time) return values[0].value;
            for (int i = 1; i < values.Length; i++) if (time <= values[i].time)
            { var a=values[i-1]; var b=values[i]; return a.value + (b.value-a.value)*(time-a.time)/(b.time-a.time); }
            return values[^1].value;
        }
    }
    public class LineRenderer : Object
    {
        public bool enabled = true, useWorldSpace = true;
        public int positionCount = 40;
        public float widthMultiplier = 0.08f;
        public Transform transform = new();
        public readonly Vector3[] Points = new Vector3[128];
        public void SetPosition(int index, Vector3 point) => Points[index] = point;
    }
}

// Contract double for the native serializable visual equation, not Spider
// gameplay. The production helper calls the real Assembly-CSharp RopeRender.
public sealed class RopeRender
{
    public float wobble, scrollSpeed, scale;
    public UnityEngine.AnimationCurve? wobbleCurve, wobbleOverLineCurve;
    public static RopeRender? Last;
    public int Calls;
    public float Phase;
    public UnityEngine.Vector3 From, To;
    public void DisplayRope(UnityEngine.Vector3 from, UnityEngine.Vector3 to, float time, UnityEngine.LineRenderer line)
    {
        Last = this; Calls++; Phase = time; From = from; To = to; line.enabled = true;
        float amplitude = 1f - Math.Clamp(time, 0, 1);
        for (int i = 0; i < line.positionCount; i++)
        {
            float f = (float)i/(line.positionCount-1);
            var p = UnityEngine.Vector3.Lerp(from, to, f);
            p += (float)Math.Cos(i * scale + time * scrollSpeed) * line.transform.up * amplitude * wobbleCurve!.Evaluate(time) * wobbleOverLineCurve!.Evaluate(f);
            line.SetPosition(i,p);
        }
    }
}
public sealed class Spider : UnityEngine.Object
{
    public UnityEngine.Transform? startPoint, spiderButt;
    public UnityEngine.LineRenderer? line;
    public RopeRender? ropeRender;
}
namespace PeakReplayLab
{
    internal sealed class VisualReplica
    {
        private readonly Dictionary<UnityEngine.Transform, UnityEngine.Transform> clones = new();
        public int LookupCount;
        public void Map(UnityEngine.Transform original, UnityEngine.Transform clone) => clones[original] = clone;
        public bool TryCloneTransform(UnityEngine.Transform original, out UnityEngine.Transform clone)
        { LookupCount++; return clones.TryGetValue(original,out clone!); }
    }
}
