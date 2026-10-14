namespace UnityEngine
{
    public class Object { public static implicit operator bool(Object? value) => value != null; }
    public class RuntimeAnimatorController : Object { }
    public class Animator : Object
    {
        public bool isActiveAndEnabled = true, Transition;
        public RuntimeAnimatorController? runtimeAnimatorController = new();
        public int layerCount = 1, StateQueries, TransitionQueries, ClipCount = 1, ClipCountQueries;
        public float speed = 1;
        public AnimatorStateInfo State = new() { fullPathHash = 1, loop = true, speed = 1, speedMultiplier = 1 };
        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) { StateQueries++; return State; }
        public bool IsInTransition(int layer) { TransitionQueries++; return Transition; }
        public int GetCurrentAnimatorClipInfoCount(int layer) { ClipCountQueries++; return ClipCount; }
    }
    public struct AnimatorStateInfo { public int fullPathHash; public bool loop; public float normalizedTime, speed, speedMultiplier; }
}

namespace PeakReplayLab
{
    // The linked production schedule/policies only copy these references. No
    // fake pose sampling is substituted for the cadence being tested.
    public sealed class ReplayFrame
    {
        public double T, SceneSampleTime;
        public bool SceneSampleTimeKnown;
        public object World = new();
        public object[] Items = [], Crates = [], Ropes = [], Effects = [], Audio = [], Spawned = [], Balloons = [], Creatures = [], Interactions = [];
    }
    public sealed class ObjectPose
    {
        public float[] Position = [], Rotation = [], Scale = [];
        public bool Active;
        public object[] Nodes = [];
    }
}
