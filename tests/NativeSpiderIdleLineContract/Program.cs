using PeakReplayLab;
using UnityEngine;

int checks = 0;
void Check(bool condition, string message)
{ checks++; if (!condition) throw new Exception(message); }
var native = new GameObject(); var visual = new VisualReplica();
var originalFrom = new Transform { position = new Vector3(99,99,99) };
var originalTo = new Transform { position = new Vector3(88,88,88) };
var from = new Transform { position = new Vector3(0,4,0) };
var to = new Transform { position = new Vector3(0,0,0) };
var line = new LineRenderer();
var spider = new Spider
{
    startPoint = originalFrom, spiderButt = originalTo, line = new LineRenderer(),
    ropeRender = new RopeRender
    {
        wobble = 0.3f, scrollSpeed = 10, scale = 0.3f,
        wobbleCurve = new AnimationCurve(new Keyframe(0,0),new Keyframe(0.5f,1),new Keyframe(1,0)) { preWrapMode=WrapMode.Loop, postWrapMode=WrapMode.PingPong },
        wobbleOverLineCurve = new AnimationCurve(new Keyframe(0,0),new Keyframe(0.5f,1),new Keyframe(1,0)),
    },
};
native.Add(spider); visual.Map(originalFrom,from); visual.Map(originalTo,to);
var helper = SpiderIdleLinePlayback.TryCreate(visual,native,line);
Check(helper != null,"audited native spider maps to inert line helper");
Check(helper!.TryApply(0),"initial no-grab idle phase accepted");
var equation = RopeRender.Last!;
Check(!ReferenceEquals(equation,spider.ropeRender),"template equation object is not reused");
Check(!ReferenceEquals(equation.wobbleCurve,spider.ropeRender!.wobbleCurve) && !ReferenceEquals(equation.wobbleOverLineCurve,spider.ropeRender.wobbleOverLineCurve),"curve snapshots isolated");
Check(equation.wobble == 0.3f && equation.scrollSpeed == 10 && equation.scale == 0.3f,"authored parameters copied");
Check(equation.wobbleCurve!.preWrapMode == WrapMode.Loop && equation.wobbleCurve.postWrapMode == WrapMode.PingPong,"curve wrapping copied");
Check(equation.From == new Vector3(0,4,0) && equation.To == new Vector3(0,0,0),"clone endpoints, not live native endpoints, used");
Check(line.Points[0] == equation.From && line.Points[39] == equation.To,"silk preserves both endpoints");
Check(helper.TryApply(0.5f) && equation.Phase == 0.5f,"post-grab fractional sinceGrab phase retained");
Check(helper.TryApply(1) && line.Points[20].x == 0 && line.Points[20].z == 0,"settled phase one remains straight");
int sourceReads = originalFrom.Reads + originalTo.Reads, componentReads = native.ComponentReads, lookups = visual.LookupCount;
int calls = equation.Calls;
bool repeated=true;for (int i=0;i<3600;i++) repeated &= helper.TryApply(0.5f);
Check(repeated,"3600 pure idle replay applications");
Check(originalFrom.Reads+originalTo.Reads == sourceReads && native.ComponentReads == componentReads && visual.LookupCount == lookups,"apply performs no source reads or directory scans");
Check(equation.Calls == calls+3600 && equation.Phase == 0.5f,"rope phase does not advance with replay frame count");
to.position = new Vector3(2,1,3); Check(helper.TryApply(0.5f) && equation.To == new Vector3(2,1,3),"silk follows abdomen after arbitrary absolute clip seek");
var saved = line.Points[20]; Check(helper.TryApply(0.5f) && line.Points[20] == saved,"paused time evaluates identically");
Check(line.widthMultiplier == 0.08f,"recorded width untouched");
foreach (float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-0.01f,1.01f})
{ calls=equation.Calls; Check(!helper.TryApply(invalid) && equation.Calls==calls,"invalid phase leaves sampled fallback untouched"); }
line.enabled=false; Check(!helper.TryApply(0.5f) && !line.enabled,"hidden line stays hidden");line.enabled=true;
line.useWorldSpace=false;Check(!helper.TryApply(0.5f),"unknown local-space template falls back");line.useWorldSpace=true;
line.positionCount=39;Check(!helper.TryApply(0.5f),"topology mismatch falls back");line.positionCount=40;
to.Destroyed=true;Check(!helper.TryApply(0.5f),"destroyed clone endpoint falls back");to.Destroyed=false;
Check(SpiderIdleLinePlayback.TryCreate(new VisualReplica(),native,line)==null,"missing mapped endpoint falls back");
spider.line!.useWorldSpace=false;Check(SpiderIdleLinePlayback.TryCreate(visual,native,line)==null,"unaudited native coordinate space rejected");spider.line.useWorldSpace=true;
spider.ropeRender!.wobbleCurve=null;Check(SpiderIdleLinePlayback.TryCreate(visual,native,line)==null,"missing authored curve rejected");
Console.WriteLine($"Native spider idle line contract: {checks} checks passed.");
