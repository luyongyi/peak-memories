namespace PeakReplayLab;

// Production checkpoint routing is linked directly into this contract. Native
// clock validation is the real RavenAnimationCapture; the geometry adapter is
// controlled so an accidental full-rig request is observable independently.
internal static class CreatureReplayRules {internal const int ActiveClock=0,IdleClock=1,SleepingClock=2;}
internal sealed class ObjectPose
{
    internal bool Active=true;
    internal float[] Position=new float[3],Rotation=new float[]{0,0,0,1},Scale=new float[]{1,1,1};
    internal object[] Nodes=System.Array.Empty<object>();
}
internal sealed class NativeRendererFrame { }
internal sealed class CreatureReplayFrame
{
    internal string Key="raven",Kind="voice-thief",Resource="voice-thief",SourcePath="native/raven";
    internal int State,ClockMode=CreatureReplayRules.IdleClock;
    internal bool SampleTimeKnown=true,SampleDiscontinuity;
    internal double SampleTime;
    internal CreatureIdleAnimation? IdleAnimation;
    internal ObjectPose Pose=new();
    internal object? Line;
    internal NativeRendererFrame[]? Visuals;
}
